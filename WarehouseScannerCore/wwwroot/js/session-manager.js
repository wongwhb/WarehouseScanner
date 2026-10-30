// session-manager.js
// 公共的Session管理工具类

class SessionManager {
    constructor(config = {}) {
        // 默认配置
        this.config = {
            sessionTimeout: 60 * 60 * 1000, // 60分钟
            heartbeatInterval: 10 * 60 * 1000, // 10分钟检查一次
            checkSessionEndpoint: '/Account/CheckSession',
            clearSessionEndpoint: '/Account/ClearSession',
            logoutEndpoint: '/Account/Logout',
            loginUrl: '/Account/Login',
            homeUrl: '/Home/Index',
            preventBackNavigation: true,
            messages: {
                confirmLogout: 'Are you sure you want to logout?',
                loggingOut: 'Logging out...',
                confirmButton: 'Confirm',
                cancelButton: 'Cancel'
            },
            ...config
        };

        // 状态变量
        this.sessionCheckInterval = null;
        this.lastActivityTime = Date.now();
        this.isCheckingSession = false;
        this.isInitialized = false;
        this.historyBlockEnabled = false;

        // 标记事件是否已绑定
        this.eventsBound = false;
        this.performLogoutHandler = null; // 存储事件处理器引用

        // DOM元素引用
        this.elements = {
            authMessage: null,
            authMessageText: null,
            sessionExpiredModal: null,
            logoutBtn: null,
            reloginBtn: null
        };

        // 绑定方法
        this.init = this.init.bind(this);
        this.performLogout = this.performLogout.bind(this);
        this.checkSessionStatus = this.checkSessionStatus.bind(this);
        this.redirectToLogin = this.redirectToLogin.bind(this);
        this.setupActivityTracking = this.setupActivityTracking.bind(this);
        this.handleVisibilityChange = this.handleVisibilityChange.bind(this);
        this.setupHistoryBlocking = this.setupHistoryBlocking.bind(this);
        this.cleanup = this.cleanup.bind(this);
    }

    // 初始化
    init() {
        if (this.isInitialized) {
            return;
        }

        // 获取DOM元素
        this.elements.authMessage = document.getElementById('authMessage');
        this.elements.authMessageText = document.getElementById('authMessageText');
        this.elements.sessionExpiredModal = document.getElementById('sessionExpiredModal');
        this.elements.logoutBtn = document.getElementById('logoutBtn');
        this.elements.reloginBtn = document.getElementById('reloginBtn');

        // 设置事件监听器（只在第一次初始化时绑定）
        if (!this.eventsBound) {
            this.setupEventListeners();
        }

        // 首次进入页面，设置本地时间戳
        this.initializeSession();

        // 设置定期检查Session状态（30秒后开始）
        setTimeout(() => {
            this.sessionCheckInterval = setInterval(
                this.checkSessionStatus.bind(this),
                this.config.heartbeatInterval
            );
        }, 30000);

        // 监听用户活动
        this.setupActivityTracking();

        // 监听页面可见性变化
        document.addEventListener('visibilitychange', this.handleVisibilityChange);

        // 防止通过后退按钮访问
        if (this.config.preventBackNavigation) {
            this.setupHistoryBlocking();
        }

        this.isInitialized = true;
    }

    // 初始化Session
    initializeSession() {
        const now = Date.now();
        localStorage.setItem('sessionTimestamp', now.toString());
        localStorage.setItem('isLoggedIn', 'true');
        sessionStorage.setItem('pageLoadTime', now.toString());
    }

    // 设置事件监听器（修复重复绑定问题）
    setupEventListeners() {
        if (this.eventsBound) {
            return;
        }

        // 登出按钮事件监听（关键修复）
        if (this.elements.logoutBtn) {
            // 创建统一的事件处理器
            this.performLogoutHandler = (event) => {
                event.preventDefault();
                event.stopPropagation();
                event.stopImmediatePropagation(); // 阻止其他事件处理器
                this.performLogout();
            };

            // 只绑定一次
            this.elements.logoutBtn.addEventListener('click', this.performLogoutHandler, { once: false });

            // 移除onclick属性，避免重复触发
            this.elements.logoutBtn.onclick = null;
        } else {
            console.warn('未找到登出按钮元素，ID: logoutBtn');
        }

        // 重新登录按钮事件监听
        if (this.elements.reloginBtn) {
            const reloginHandler = (event) => {
                event.preventDefault();
                event.stopPropagation();
                this.redirectToLogin();
            };

            this.elements.reloginBtn.addEventListener('click', reloginHandler);
        }

        // 页面卸载时清理
        window.addEventListener('beforeunload', this.cleanup);

        // 监听页面加载事件
        window.addEventListener('load', this.onPageLoad.bind(this));

        // 标记事件已绑定
        this.eventsBound = true;
    }

    // 页面加载时检查
    onPageLoad() {
        // 检查当前页面是否为登录页
        const isLoginPage = window.location.pathname.toLowerCase().includes('/account/login') ||
            window.location.pathname.toLowerCase().includes('/login');

        if (isLoginPage) {
            this.clearLocalAuthData();
        } else {
            // 非登录页面，检查Session状态
            this.checkInitialSession();
        }
    }

    // 初始Session检查
    async checkInitialSession() {
        // 检查本地登录状态
        const isLoggedIn = localStorage.getItem('isLoggedIn');

        if (!isLoggedIn || isLoggedIn !== 'true') {
            this.redirectToLogin();
            return;
        }

        // 正常Session检查
        await this.checkSessionStatus();
    }

    // 检查Session状态
    async checkSessionStatus(forceCheck = false) {
        // 防止重复检查
        if (this.isCheckingSession && !forceCheck) {
            return true;
        }

        this.isCheckingSession = true;

        try {
            // 首先检查本地登录状态
            const isLoggedIn = localStorage.getItem('isLoggedIn');
            if (!isLoggedIn || isLoggedIn !== 'true') {
                this.redirectToLogin();
                return false;
            }

            // 检查本地存储的时间戳
            const sessionTimestamp = localStorage.getItem('sessionTimestamp');
            const currentTime = Date.now();

            // 如果本地没有时间戳，说明Session已过期
            if (!sessionTimestamp) {
                this.handleUnauthenticated();
                return false;
            }

            // 检查是否超过Session超时时间
            const timeDiff = currentTime - parseInt(sessionTimestamp);

            if (timeDiff > this.config.sessionTimeout) {
                this.clearLocalAuthData();
                this.showSessionExpiredModal();
                return false;
            }

            // 如果时间差小于30秒，不检查服务器状态（防止频繁请求）
            if (timeDiff < 30000 && !forceCheck) {
                this.lastActivityTime = currentTime;
                return true;
            }

            // 发送心跳请求到服务器检查Session
            const response = await fetch(this.config.checkSessionEndpoint, {
                method: 'GET',
                headers: {
                    'Cache-Control': 'no-cache',
                    'Pragma': 'no-cache',
                    'X-Requested-With': 'XMLHttpRequest'
                },
                credentials: 'include'
            });

            if (response.ok) {
                const result = await response.json();

                if (result.isAuthenticated) {
                    this.lastActivityTime = Date.now();
                    localStorage.setItem('sessionTimestamp', Date.now().toString());
                    localStorage.setItem('isLoggedIn', 'true');
                    return true;
                } else {
                    this.handleUnauthenticated();
                    return false;
                }
            } else {
                // 如果服务器请求失败，但本地时间还在有效期内，暂时相信本地Session
                if (timeDiff < this.config.sessionTimeout) {
                    return true;
                } else {
                    this.handleUnauthenticated();
                    return false;
                }
            }

        } catch (error) {
            console.error('检查Session时发生错误:', error);

            // 错误处理：获取本地时间戳
            const sessionTimestamp = localStorage.getItem('sessionTimestamp');
            const currentTime = Date.now();

            if (sessionTimestamp) {
                const timeDiff = currentTime - parseInt(sessionTimestamp);

                // 如果本地时间还在有效期内，暂时相信本地Session
                if (timeDiff < this.config.sessionTimeout) {
                    return true;
                }
            }

            this.showAuthMessage((window.appTexts && window.appTexts.networkConnError) || '网络连接异常，请检查后重试');
            return false;
        } finally {
            this.isCheckingSession = false;
        }
    }

    // 处理未认证状态
    handleUnauthenticated() {
        this.clearLocalAuthData();
        this.showSessionExpiredModal();
    }

    // 清除本地认证数据
    clearLocalAuthData() {
        localStorage.removeItem('sessionTimestamp');
        localStorage.removeItem('isLoggedIn');
        sessionStorage.removeItem('pageLoadTime');
        sessionStorage.clear();
    }

    // 显示认证消息
    showAuthMessage(message) {
        // 检查元素是否存在
        if (this.elements.authMessage && this.elements.authMessageText) {
            this.elements.authMessageText.textContent = message;
            this.elements.authMessage.classList.add('show');

            setTimeout(() => {
                this.elements.authMessage.classList.remove('show');
            }, 5000);
        } else {
            // 如果元素不存在，使用SweetAlert2作为备用方案
            Swal.fire({ icon: 'warning', text: message, timer: 3000, timerProgressBar: true });
        }
    }

    // 显示Session过期模态框
    showSessionExpiredModal() {
        if (this.elements.sessionExpiredModal) {
            this.elements.sessionExpiredModal.style.display = 'flex';
        } else {
            this.redirectToLogin();
        }
    }

    // 重定向到登录页面
    redirectToLogin(returnUrl = window.location.pathname) {
        // 清除所有认证数据
        this.clearLocalAuthData();

        // 添加标志，表示这是主动重定向
        sessionStorage.setItem('redirectedToLogin', 'true');

        // 使用location.replace防止后退
        window.location.replace(`${this.config.loginUrl}?returnUrl=${encodeURIComponent(returnUrl)}&t=${Date.now()}`);
    }

    // 显示退出确认对话框
    async showLogoutConfirm() {
        if (window.Swal) {
            const result = await Swal.fire({
                title: this.config.messages.confirmLogout,
                icon: 'question',
                showCancelButton: true,
                confirmButtonColor: '#3085d6',
                cancelButtonColor: '#d33',
                confirmButtonText: 'Confirm',
                cancelButtonText: 'Cancel'
            });
            return result.isConfirmed;
        } else {
            return confirm(this.config.messages.confirmLogout);
        }
    }

    // 执行退出（简化版，避免重复确认）
    async performLogout() {
        // 防止重复执行
        if (this.isLoggingOut) {
            return;
        }

        this.isLoggingOut = true;

        try {
            // 只显示一次确认对话框
            if (!this.logoutConfirmed) {
                const confirmResult = await this.showLogoutConfirm();
                if (!confirmResult) {
                    this.isLoggingOut = false;
                    return;
                }
                this.logoutConfirmed = true;
            }

            // 清除所有本地数据
            this.clearLocalAuthData();

            // 添加退出标志
            sessionStorage.setItem('userLoggedOut', 'true');

            // 显示退出中提示
            this.showAuthMessage(this.config.messages.loggingOut);

            // 发送退出请求到服务器
            // Read AntiForgery token from hidden input generated by @Html.AntiForgeryToken()
            // Without this token, POST to Logout is rejected with 400 by [ValidateAntiForgeryToken]
            // causing Logout action to never execute and Session/Cookie to not be cleaned up.
            const tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
            const headers = { 'Content-Type': 'application/json' };
            if (tokenInput) {
                headers['X-CSRF-TOKEN'] = tokenInput.value;
            }

            const response = await fetch(this.config.logoutEndpoint, {
                method: 'POST',
                headers: headers,
                credentials: 'include'
            });

            // 短暂延迟确保数据清除完成
            await new Promise(resolve => setTimeout(resolve, 500));

            // 使用replace防止后退到已退出的页面
            window.location.replace(`${this.config.loginUrl}?logout=true&t=${Date.now()}`);

        } catch (error) {
            console.error('退出时发生错误:', error);
            // 即使出错也重定向到登录页
            window.location.replace(`${this.config.loginUrl}?logout=true&t=${Date.now()}`);
        } finally {
            // 重置退出状态
            setTimeout(() => {
                this.isLoggingOut = false;
                this.logoutConfirmed = false;
            }, 1000);
        }
    }

    // 设置活动追踪
    setupActivityTracking() {
        const activityEvents = ['mousemove', 'keydown', 'click', 'scroll', 'touchstart'];

        activityEvents.forEach(event => {
            document.addEventListener(event, () => {
                const now = Date.now();
                this.lastActivityTime = now;
                // 每次活动都更新时间戳和登录状态
                localStorage.setItem('sessionTimestamp', now.toString());
                localStorage.setItem('isLoggedIn', 'true');
            }, { passive: true });
        });

        // 检查活动超时（每分钟检查一次）
        setInterval(() => {
            const sessionTimestamp = localStorage.getItem('sessionTimestamp');
            if (!sessionTimestamp) return;

            const currentTime = Date.now();
            const timeDiff = currentTime - parseInt(sessionTimestamp);

            if (timeDiff > this.config.sessionTimeout) {
                this.handleUnauthenticated();
            }
        }, 60000);
    }

    // 处理页面可见性变化
    handleVisibilityChange() {
        if (document.visibilityState === 'visible') {
            this.checkSessionStatus();
        }
    }

    // 设置历史记录阻止
    setupHistoryBlocking() {
        if (window.history && window.history.pushState) {
            // 初始状态
            window.history.pushState({ page: 'current' }, '', window.location.href);
            this.historyBlockEnabled = true;

            // 监听popstate事件（后退/前进按钮）
            window.addEventListener('popstate', (event) => {
                if (!this.historyBlockEnabled) return;

                // 检查Session状态
                this.checkSessionStatus(true).then(isValid => {
                    if (!isValid) {
                        window.history.pushState({ page: 'current' }, '', window.location.href);
                        this.showAuthMessage((window.appTexts && window.appTexts.sessionExpiredRelogin) || '会话已过期，请重新登录');
                    } else {
                        window.history.pushState({ page: 'current' }, '', window.location.href);
                    }
                });
            });

            // 监听beforeunload事件，确保离开时清除状态
            window.addEventListener('beforeunload', () => {
                this.historyBlockEnabled = false;
            });
        }
    }

    // 清理函数
    cleanup() {
        if (this.sessionCheckInterval) {
            clearInterval(this.sessionCheckInterval);
        }
        this.historyBlockEnabled = false;
    }

    // 手动检查Session状态（供外部调用）
    async manualCheckSession() {
        try {
            const result = await this.checkSessionStatus();
            // 即使 checkSessionStatus 返回 false，也允许用户操作（由后端控制器进行最终鉴权）
            return true;
        } catch (error) {
            console.error('manualCheckSession 异常:', error);
            // 异常时也允许用户操作
            return true;
        }
    }

    // 临时禁用 history blocking（用于真正的页面跳转）
    disableHistoryBlockingTemporarily() {
        this.historyBlockEnabled = false;
        // 500ms 后恢复（防止意外情况）
        setTimeout(() => {
            this.historyBlockEnabled = true;
        }, 500);
    }

    // 获取Session状态信息
    getSessionInfo() {
        const sessionTimestamp = localStorage.getItem('sessionTimestamp');
        const isLoggedIn = localStorage.getItem('isLoggedIn');
        const currentTime = Date.now();

        if (!sessionTimestamp || !isLoggedIn || isLoggedIn !== 'true') {
            return {
                isAuthenticated: false,
                timeRemaining: 0,
                timeRemainingSeconds: 0,
                lastActivity: null,
                isExpired: true,
                isLoggedIn: false
            };
        }

        const timeDiff = currentTime - parseInt(sessionTimestamp);
        const timeRemaining = Math.max(0, this.config.sessionTimeout - timeDiff);

        return {
            isAuthenticated: timeDiff < this.config.sessionTimeout,
            timeRemaining: timeRemaining,
            timeRemainingSeconds: Math.floor(timeRemaining / 1000),
            lastActivity: new Date(parseInt(sessionTimestamp)),
            isExpired: timeDiff > this.config.sessionTimeout,
            isLoggedIn: true
        };
    }

    // 重置Session时间（延长Session有效期）
    resetSession() {
        const now = Date.now();
        localStorage.setItem('sessionTimestamp', now.toString());
        localStorage.setItem('isLoggedIn', 'true');
        this.lastActivityTime = now;
        return now;
    }
}

// 创建全局Session管理器实例（单例模式）
if (!window.SessionManager) {
    const sessionMessages = window.sessionMessages || {
        confirmLogout: 'Are you sure you want to logout?',
        loggingOut: 'Logging out...',
        confirmButton: 'Confirm',
        cancelButton: 'Cancel'
    };
    window.SessionManager = new SessionManager({
        homeUrl: '/Home/Index',
        preventBackNavigation: true,
        messages: sessionMessages
    });

    // 页面加载完成后自动初始化（只执行一次）
    let managerInitialized = false;
    document.addEventListener('DOMContentLoaded', () => {
        if (!managerInitialized) {
            window.SessionManager.init();
            managerInitialized = true;
        }
    });
}
