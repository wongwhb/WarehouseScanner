// 平板设备优化脚本
(function () {
    'use strict';

    // 设备检测
    const isTablet = /ipad|android(?!.*mobile)|tablet/i.test(navigator.userAgent);
    const isMobile = /mobile/i.test(navigator.userAgent);
    const isTouchDevice = 'ontouchstart' in window;

    // 初始化函数
    function init() {
        // 添加设备类到body
        if (isTablet) {
            document.body.classList.add('tablet-device');
        }
        if (isTouchDevice) {
            document.body.classList.add('touch-device');
        }

        // 初始化触摸反馈
        initTouchFeedback();

        // 初始化扫码功能
        initScanFeatures();

        // 初始化离线检测
        initOfflineDetection();

        // 初始化添加到主屏幕
        initAddToHomeScreen();

        // 初始化键盘事件处理
        initKeyboardEvents();
    }

    // 触摸反馈
    function initTouchFeedback() {
        const touchElements = document.querySelectorAll('.btn, .nav-item, .action-btn, .lang-btn');

        touchElements.forEach(element => {
            element.addEventListener('touchstart', function () {
                this.style.opacity = '0.8';
                this.style.transform = 'scale(0.98)';
            });

            element.addEventListener('touchend', function () {
                this.style.opacity = '1';
                this.style.transform = 'scale(1)';
            });

            element.addEventListener('touchcancel', function () {
                this.style.opacity = '1';
                this.style.transform = 'scale(1)';
            });
        });

        // 防止双击缩放
        let lastTouchEnd = 0;
        document.addEventListener('touchend', function (event) {
            const now = Date.now();
            if (now - lastTouchEnd <= 300) {
                event.preventDefault();
            }
            lastTouchEnd = now;
        }, { passive: false });
    }

    // 扫码功能
    function initScanFeatures() {
        // 扫码枪键盘事件处理
        let barcodeBuffer = '';
        let lastKeyTime = 0;
        const barcodeTimeout = 100; // 100ms

        document.addEventListener('keydown', function (e) {
            // 忽略功能键
            if (e.key.length === 1 && /[0-9]/.test(e.key)) {
                const currentTime = Date.now();

                // 如果超过超时时间，清空缓冲区
                if (currentTime - lastKeyTime > barcodeTimeout) {
                    barcodeBuffer = '';
                }

                barcodeBuffer += e.key;
                lastKeyTime = currentTime;
            }

            // 回车键表示扫码完成
            if (e.key === 'Enter' && barcodeBuffer.length >= 8) {
                const barcodeInput = document.querySelector('input[type="text"][name*="barcode"], input[type="text"][id*="barcode"]');
                if (barcodeInput) {
                    barcodeInput.value = barcodeBuffer;

                    // 触发扫码事件
                    const scanEvent = new CustomEvent('scancomplete', {
                        detail: { barcode: barcodeBuffer }
                    });
                    document.dispatchEvent(scanEvent);

                    // 自动提交表单（如果有扫码按钮）
                    const scanButton = document.querySelector('button[type="submit"], button[name*="scan"]');
                    if (scanButton) {
                        setTimeout(() => {
                            scanButton.click();
                        }, 100);
                    }
                }

                barcodeBuffer = '';
                e.preventDefault();
            }
        });
    }

    // 显示消息
    function showMessage(text, type = 'info') {
        // 移除现有消息
        const existingMessage = document.querySelector('.custom-toast');
        if (existingMessage) {
            existingMessage.remove();
        }

        // 创建消息元素
        const messageDiv = document.createElement('div');
        messageDiv.className = `custom-toast ${type}`;
        messageDiv.innerHTML = `
            <i class="fas fa-${type === 'success' ? 'check-circle' : 'info-circle'}"></i>
            <span>${text}</span>
        `;

        // 样式
        messageDiv.style.cssText = `
            position: fixed;
            top: 90px;
            left: 50%;
            transform: translateX(-50%);
            background: white;
            padding: 1rem 1.5rem;
            border-radius: 10px;
            box-shadow: 0 4px 12px rgba(0,0,0,0.15);
            display: flex;
            align-items: center;
            gap: 0.75rem;
            z-index: 9999;
            animation: slideIn 0.3s ease;
            border-left: 4px solid ${type === 'success' ? '#48bb78' : '#4299e1'};
        `;

        document.body.appendChild(messageDiv);

        // 3秒后自动移除
        setTimeout(() => {
            messageDiv.style.opacity = '0';
            messageDiv.style.transform = 'translate(-50%, -20px)';
            setTimeout(() => {
                if (messageDiv.parentNode) {
                    messageDiv.parentNode.removeChild(messageDiv);
                }
            }, 300);
        }, 3000);
    }

    // 语言切换
    function changeLanguage(language) {
        // 显示加载状态
        const originalTitle = document.title;
        document.title = (window.appTexts && window.appTexts.switchingLanguage) || '切换语言中...';

        // 发送请求切换语言
        fetch(`/Account/ChangeLanguage?language=${language}`, {
            method: 'GET',
            headers: {
                'Accept': 'application/json',
            }
        })
            .then(response => {
                if (response.ok) {
                    // 重新加载页面
                    window.location.reload();
                } else {
                    showMessage((window.appTexts && window.appTexts.languageSwitchFailed) || '语言切换失败', 'error');
                    document.title = originalTitle;
                }
            })
            .catch(error => {
                console.error('语言切换错误:', error);
                showMessage((window.appTexts && window.appTexts.networkErrorRetry) || '网络错误，请重试', 'error');
                document.title = originalTitle;
            });
    }

    // 离线检测
    function initOfflineDetection() {
        window.addEventListener('online', function () {
            showMessage((window.appTexts && window.appTexts.networkRestored) || '网络已恢复', 'success');
        });

        window.addEventListener('offline', function () {
            showMessage((window.appTexts && window.appTexts.networkOffline) || '网络已断开，部分功能可能受限', 'error');
        });

        // 初始状态检查
        if (!navigator.onLine) {
            setTimeout(() => {
                showMessage((window.appTexts && window.appTexts.offlineDataSaved) || '当前处于离线状态，数据将保存在本地', 'info');
            }, 1000);
        }
    }

    // 添加到主屏幕
    let deferredPrompt;

    function initAddToHomeScreen() {
        window.addEventListener('beforeinstallprompt', (e) => {
            // 阻止默认提示
            e.preventDefault();
            deferredPrompt = e;

            // 显示自定义安装按钮
            showInstallButton();
        });

        // 检测是否已安装
        window.addEventListener('appinstalled', () => {
            deferredPrompt = null;
            hideInstallButton();
        });
    }

    function showInstallButton() {
        // 检查是否已经有安装按钮
        if (document.getElementById('install-button')) return;

        const installButton = document.createElement('button');
        installButton.id = 'install-button';
        installButton.innerHTML = '<i class="fas fa-download"></i> ' + ((window.appTexts && window.appTexts.addToHomeScreen) || '添加到主屏幕');
        installButton.className = 'btn btn-primary';
        installButton.style.cssText = `
            position: fixed;
            bottom: 90px;
            right: 20px;
            z-index: 1000;
            box-shadow: 0 4px 12px rgba(0,0,0,0.15);
            animation: bounce 2s infinite;
        `;

        installButton.onclick = async () => {
            if (deferredPrompt) {
                deferredPrompt.prompt();
                const { outcome } = await deferredPrompt.userChoice;
                if (outcome === 'accepted') {
                    showMessage((window.appTexts && window.appTexts.addedToHomeScreen) || '已添加到主屏幕', 'success');
                }
                deferredPrompt = null;
                hideInstallButton();
            }
        };

        document.body.appendChild(installButton);

        // 添加动画
        const style = document.createElement('style');
        style.textContent = `
            @keyframes bounce {
                0%, 20%, 50%, 80%, 100% {transform: translateY(0);}
                40% {transform: translateY(-10px);}
                60% {transform: translateY(-5px);}
            }
        `;
        document.head.appendChild(style);
    }

    function hideInstallButton() {
        const installButton = document.getElementById('install-button');
        if (installButton) {
            installButton.remove();
        }
    }

    // 键盘事件处理
    function initKeyboardEvents() {
        // 防止表单重复提交
        const forms = document.querySelectorAll('form');
        forms.forEach(form => {
            let isSubmitting = false;

            form.addEventListener('submit', function (e) {
                if (isSubmitting) {
                    e.preventDefault();
                    return false;
                }

                isSubmitting = true;

                // 显示加载状态
                const submitButton = this.querySelector('button[type="submit"]');
                if (submitButton) {
                    const originalText = submitButton.innerHTML;
                    submitButton.disabled = true;
                    submitButton.innerHTML = '<span class="spinner"></span> ' + ((window.appTexts && window.appTexts.processing) || '处理中...');

                    // 表单提交后恢复按钮状态
                    setTimeout(() => {
                        submitButton.disabled = false;
                        submitButton.innerHTML = originalText;
                        isSubmitting = false;
                    }, 3000);
                }

                return true;
            });
        });
    }

    // 设备方向变化
    window.addEventListener('orientationchange', function () {
        // 重新调整布局
        setTimeout(() => {
            window.scrollTo(0, 0);
        }, 300);
    });

    // 页面可见性变化
    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'visible') {
            // 页面变为可见，可以刷新数据
        }
    });

    // 导出全局函数
    window.showMessage = showMessage;
    window.changeLanguage = changeLanguage;

    // 初始化
    document.addEventListener('DOMContentLoaded', init);
})();