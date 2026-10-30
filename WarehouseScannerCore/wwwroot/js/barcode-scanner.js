/**
 * BarcodeScanner - 扫码枪扫描公共管理类
 * 提供统一的硬件扫码枪扫描功能，支持键盘模拟的USB扫码枪
 * @version 1.0.0
 */
class BarcodeScanner {
    constructor(options = {}) {
        // 默认配置
        this.defaultConfig = {
            minLength: 3,           // 最小条码长度
            maxLength: 100,         // 最大条码长度
            timeout: 150,           // 输入超时时间（毫秒）
            scanInterval: 50,       // 扫码枪输入间隔（毫秒）
            endChar: '\r',          // 结束字符（回车）
            endKey: 'Enter',        // 结束键
            soundEnabled: true,     // 启用声音反馈
            autoFocus: true,        // 自动聚焦隐藏输入框
            vibrationEnabled: true  // 启用震动反馈
        };

        // 合并配置
        this.config = { ...this.defaultConfig, ...options.config };

        // 回调函数
        this.onScanSuccess = options.onScanSuccess || function (barcodeText) {
        };

        this.onScanError = options.onScanError || function (error) {
            console.debug('扫码枪扫描错误:', error);
        };

        this.onScannerModeChange = options.onScannerModeChange || function (isEnabled) {
        };

        // 状态变量
        this.isScannerEnabled = false;
        this.barcodeBuffer = '';
        this.barcodeTimer = null;
        this.lastKeyTime = 0;
        this.currentFocusField = null;

        // DOM 元素引用
        this.barcodeInputHidden = null;
        this.barcodeInputHint = null;
        this.scannerModeIndicator = null;

        // 绑定方法上下文
        this.enableScanner = this.enableScanner.bind(this);
        this.disableScanner = this.disableScanner.bind(this);
        this.handleKeyDown = this.handleKeyDown.bind(this);
        this.handleKeyPress = this.handleKeyPress.bind(this);
        this.processBarcodeInput = this.processBarcodeInput.bind(this);
        this.resetBarcodeBuffer = this.resetBarcodeBuffer.bind(this);
        this.playBarcodeBeep = this.playBarcodeBeep.bind(this);
        this.showBarcodeHint = this.showBarcodeHint.bind(this);
        this.hideBarcodeHint = this.hideBarcodeHint.bind(this);
        this.setFocusField = this.setFocusField.bind(this);
        this.clearFocusField = this.clearFocusField.bind(this);
    }

    /**
     * 初始化扫码枪管理器
     * @param {Object} elements - DOM元素配置
     */
    initialize(elements = {}) {
        // 创建或获取隐藏的扫码枪输入框
        this.barcodeInputHidden = elements.barcodeInputHidden || document.getElementById('barcodeInputHidden');
        if (!this.barcodeInputHidden) {
            this.barcodeInputHidden = this.createHiddenInput();
        }

        // 创建或获取扫码枪提示元素
        this.barcodeInputHint = elements.barcodeInputHint || document.getElementById('barcodeInputHint');
        if (!this.barcodeInputHint) {
            this.barcodeInputHint = this.createHintElement();
        }

        // 创建或获取扫描模式指示器
        this.scannerModeIndicator = elements.scannerModeIndicator || document.getElementById('scannerModeIndicator');

        // 初始化CSS样式
        this.initializeStyles();

        return true;
    }

    /**
     * 创建隐藏输入框
     */
    createHiddenInput() {
        const input = document.createElement('input');
        input.type = 'text';
        input.id = 'barcodeInputHidden';
        input.className = 'barcode-input-hidden';
        input.setAttribute('autocomplete', 'off');
        input.setAttribute('autocapitalize', 'off');
        input.setAttribute('spellcheck', 'false');
        document.body.appendChild(input);
        return input;
    }

    /**
     * 创建提示元素
     */
    createHintElement() {
        const hint = document.createElement('div');
        hint.id = 'barcodeInputHint';
        hint.className = 'barcode-input-hint';
        hint.innerHTML = '<i class="fas fa-barcode"></i> <span>' + ((window.appTexts && window.appTexts.scannerReady) || '扫码枪就绪，请扫描条码...') + '</span>';
        document.body.appendChild(hint);
        return hint;
    }

    /**
     * 初始化CSS样式
     */
    initializeStyles() {
        // 只在没有相应样式时添加
        if (!document.getElementById('barcode-scanner-styles')) {
            const style = document.createElement('style');
            style.id = 'barcode-scanner-styles';
            style.textContent = `
                /* 隐藏的扫码枪输入框 */
                .barcode-input-hidden {
                    position: absolute;
                    left: -1000px;
                    top: -1000px;
                    width: 1px;
                    height: 1px;
                    opacity: 0;
                    pointer-events: none;
                }

                /* 扫码枪输入提示 */
                .barcode-input-hint {
                    position: fixed;
                    bottom: 20px;
                    left: 50%;
                    transform: translateX(-50%);
                    background: rgba(7, 193, 96, 0.9);
                    color: white;
                    padding: 10px 20px;
                    border-radius: 25px;
                    font-size: 0.9rem;
                    z-index: 100;
                    display: none;
                    align-items: center;
                    gap: 10px;
                    box-shadow: 0 5px 15px rgba(7, 193, 96, 0.3);
                }

                .barcode-input-hint.show {
                    display: flex;
                    animation: fadeInUp 0.3s ease-out;
                }

                @keyframes fadeInUp {
                    from {
                        opacity: 0;
                        transform: translate(-50%, 20px);
                    }
                    to {
                        opacity: 1;
                        transform: translate(-50%, 0);
                    }
                }

                /* 扫描模式指示器 */
                .scanner-mode-indicator {
                    position: fixed;
                    top: 60px;
                    right: 10px;
                    background: rgba(0, 0, 0, 0.8);
                    color: white;
                    padding: 5px 15px;
                    border-radius: 15px;
                    font-size: 0.8rem;
                    z-index: 100;
                    display: flex;
                    align-items: center;
                    gap: 8px;
                    cursor: pointer;
                    transition: all 0.3s;
                }

                .scanner-mode-indicator:hover {
                    background: rgba(0, 0, 0, 0.9);
                    transform: translateY(-2px);
                }

                .scanner-mode-icon {
                    font-size: 1rem;
                }

                .scanner-mode-text {
                    font-weight: 500;
                }

                /* 扫码模式切换按钮 */
                .scanner-mode-toggle {
                    display: flex;
                    align-items: center;
                    gap: 10px;
                    margin-bottom: 15px;
                    background: #f8f9fa;
                    padding: 10px 15px;
                    border-radius: 8px;
                }

                .scanner-mode-toggle label {
                    font-weight: 500;
                    color: #333;
                }

                .mode-switch {
                    position: relative;
                    display: inline-block;
                    width: 60px;
                    height: 30px;
                }

                .mode-switch input {
                    opacity: 0;
                    width: 0;
                    height: 0;
                }

                .mode-slider {
                    position: absolute;
                    cursor: pointer;
                    top: 0;
                    left: 0;
                    right: 0;
                    bottom: 0;
                    background-color: #ccc;
                    transition: .4s;
                    border-radius: 34px;
                }

                .mode-slider:before {
                    position: absolute;
                    content: "";
                    height: 22px;
                    width: 22px;
                    left: 4px;
                    bottom: 4px;
                    background-color: white;
                    transition: .4s;
                    border-radius: 50%;
                }

                input:checked + .mode-slider {
                    background-color: #07c160;
                }

                input:checked + .mode-slider:before {
                    transform: translateX(30px);
                }

                .mode-label {
                    display: flex;
                    align-items: center;
                    gap: 5px;
                    font-size: 0.9rem;
                }

                /* 响应式优化 */
                @media (max-width: 768px) {
                    .scanner-mode-indicator {
                        top: 50px;
                        right: 5px;
                        padding: 3px 10px;
                        font-size: 0.7rem;
                    }

                    .barcode-input-hint {
                        font-size: 0.8rem;
                        padding: 8px 16px;
                    }
                }

                @media (max-width: 480px) {
                    .scanner-mode-indicator {
                        top: 40px;
                        padding: 2px 8px;
                        font-size: 0.6rem;
                    }

                    .barcode-input-hint {
                        font-size: 0.75rem;
                        padding: 6px 12px;
                    }
                }
            `;
            document.head.appendChild(style);
        }
    }

    /**
     * 启用扫码枪扫描
     */
    enableScanner() {
        if (this.isScannerEnabled) return;

        // 设置状态
        this.isScannerEnabled = true;

        // 隐藏的输入框获得焦点
        if (this.config.autoFocus && this.barcodeInputHidden) {
            this.barcodeInputHidden.focus();
        }

        // 监听全局键盘事件
        document.addEventListener('keydown', this.handleKeyDown);
        document.addEventListener('keypress', this.handleKeyPress);

        // 显示扫码枪提示
        this.showBarcodeHint((window.appTexts && window.appTexts.scannerReady) || '扫码枪就绪，请扫描条码...');

        // 调用模式切换回调
        if (typeof this.onScannerModeChange === 'function') {
            this.onScannerModeChange(true);
        }

        // 定期检查隐藏输入框的焦点
        this.startFocusMonitor();
    }

    /**
     * 禁用扫码枪扫描
     */
    disableScanner() {
        if (!this.isScannerEnabled) return;

        // 设置状态
        this.isScannerEnabled = false;

        // 移除键盘事件监听
        document.removeEventListener('keydown', this.handleKeyDown);
        document.removeEventListener('keypress', this.handleKeyPress);

        // 清空缓冲区
        this.resetBarcodeBuffer();

        // 隐藏扫码枪提示
        this.hideBarcodeHint();

        // 停止焦点监控
        this.stopFocusMonitor();

        // 调用模式切换回调
        if (typeof this.onScannerModeChange === 'function') {
            this.onScannerModeChange(false);
        }
    }

    /**
     * 处理按键按下事件
     */
    handleKeyDown(event) {
        // 检查是否是扫码枪结束键（回车）
        if (event.key === this.config.endKey) {
            event.preventDefault();
            event.stopPropagation();

            if (this.barcodeBuffer.length >= this.config.minLength) {
                this.processBarcodeInput();
            } else {
                this.resetBarcodeBuffer();
            }
            return false;
        }

        // 记录按键时间
        this.lastKeyTime = Date.now();
    }

    /**
     * 处理按键输入事件
     */
    handleKeyPress(event) {
        // 获取输入的字符
        const char = String.fromCharCode(event.which || event.keyCode);
        const now = Date.now();

        // 检查是否是快速输入（扫码枪特征）
        if (now - this.lastKeyTime < this.config.scanInterval) {
            event.preventDefault();
            event.stopPropagation();

            // 添加到缓冲区
            this.barcodeBuffer += char;

            // 重置超时计时器
            this.resetBarcodeTimer();

            // 设置新的超时计时器
            this.barcodeTimer = setTimeout(() => {
                if (this.barcodeBuffer.length >= this.config.minLength) {
                    this.processBarcodeInput();
                } else {
                    this.resetBarcodeBuffer();
                }
            }, this.config.timeout);

            // 更新最后按键时间
            this.lastKeyTime = now;

            return false;
        }

        // 如果输入间隔过长，清空缓冲区（可能是手动输入）
        this.resetBarcodeBuffer();
    }

    /**
     * 处理扫码枪输入
     */
    processBarcodeInput() {
        if (this.barcodeBuffer.length < this.config.minLength) {
            this.resetBarcodeBuffer();
            return;
        }

        // 播放提示音
        if (this.config.soundEnabled) {
            this.playBarcodeBeep();
        }

        // 震动反馈
        if (this.config.vibrationEnabled && navigator.vibrate) {
            navigator.vibrate(30);
        }

        // 显示成功反馈
        this.showBarcodeHint((window.appTexts && window.appTexts.scanSuccess) || '扫码枪扫描成功！', 1500);

        // 调用成功回调
        if (typeof this.onScanSuccess === 'function') {
            this.onScanSuccess(this.barcodeBuffer, this.currentFocusField);
        }

        // 清空缓冲区
        this.resetBarcodeBuffer();
    }

    /**
     * 重置扫码枪缓冲区
     */
    resetBarcodeBuffer() {
        this.barcodeBuffer = '';
        if (this.barcodeTimer) {
            clearTimeout(this.barcodeTimer);
            this.barcodeTimer = null;
        }
    }

    /**
     * 重置扫码枪计时器
     */
    resetBarcodeTimer() {
        if (this.barcodeTimer) {
            clearTimeout(this.barcodeTimer);
            this.barcodeTimer = null;
        }
    }

    /**
     * 播放扫码枪提示音
     */
    playBarcodeBeep() {
        try {
            const audioContext = new (window.AudioContext || window.webkitAudioContext)();
            const oscillator = audioContext.createOscillator();
            const gainNode = audioContext.createGain();

            oscillator.connect(gainNode);
            gainNode.connect(audioContext.destination);

            oscillator.frequency.value = 1500;
            oscillator.type = 'sine';

            gainNode.gain.setValueAtTime(0.2, audioContext.currentTime);
            gainNode.gain.exponentialRampToValueAtTime(0.01, audioContext.currentTime + 0.1);

            oscillator.start(audioContext.currentTime);
            oscillator.stop(audioContext.currentTime + 0.1);
        } catch (e) {
            console.debug('扫码枪音频播放失败:', e);
        }
    }

    /**
     * 显示扫码枪提示
     * @param {string} message - 提示消息
     * @param {number} duration - 显示持续时间（毫秒），默认5000
     */
    showBarcodeHint(message, duration = 5000) {
        if (!this.barcodeInputHint) return;

        const span = this.barcodeInputHint.querySelector('span');
        if (span) {
            span.textContent = message;
        }

        this.barcodeInputHint.classList.add('show');

        // 设置定时隐藏
        setTimeout(() => {
            this.hideBarcodeHint();
        }, duration);
    }

    /**
     * 隐藏扫码枪提示
     */
    hideBarcodeHint() {
        if (!this.barcodeInputHint) return;
        this.barcodeInputHint.classList.remove('show');
    }

    /**
     * 设置当前焦点字段
     * @param {string} fieldId - 字段ID
     * @param {string} fieldName - 字段名称（可选）
     */
    setFocusField(fieldId, fieldName = '') {
        this.currentFocusField = fieldId;

        // 显示对应的提示
        if (fieldName) {
            this.showBarcodeHint(((window.appTexts && window.appTexts.scannerReady) || '扫码枪就绪，请扫描{fieldName}条码...').replace('{fieldName}', fieldName));
        }
    }

    /**
     * 清除当前焦点字段
     */
    clearFocusField() {
        this.currentFocusField = null;
        // 延迟隐藏提示，避免快速切换时闪烁
        setTimeout(() => {
            if (!this.currentFocusField) {
                this.hideBarcodeHint();
            }
        }, 100);
    }

    /**
     * 开始焦点监控
     */
    startFocusMonitor() {
        this.stopFocusMonitor();
        this.focusMonitorInterval = setInterval(() => {
            if (this.isScannerEnabled && this.barcodeInputHidden) {
                if (document.activeElement !== this.barcodeInputHidden) {
                    this.barcodeInputHidden.focus();
                }
            }
        }, 1000);
    }

    /**
     * 停止焦点监控
     */
    stopFocusMonitor() {
        if (this.focusMonitorInterval) {
            clearInterval(this.focusMonitorInterval);
            this.focusMonitorInterval = null;
        }
    }

    /**
     * 创建扫描模式切换UI
     * @param {Object} container - 容器元素
     * @param {Object} options - 选项
     * @returns {Object} UI元素
     */
    createModeToggleUI(container, options = {}) {
        const defaultOptions = {
            label: (window.appTexts && window.appTexts.scanModeLabel) || '扫描模式：',
            defaultMode: 'barcode', // 'barcode' 或 'camera'
            onChange: null
        };

        const opts = { ...defaultOptions, ...options };

        // 创建容器
        const toggleContainer = document.createElement('div');
        toggleContainer.className = 'scanner-mode-toggle';

        // 创建标签
        const label = document.createElement('label');
        label.textContent = opts.label;
        toggleContainer.appendChild(label);

        // 创建开关
        const switchContainer = document.createElement('div');
        switchContainer.className = 'mode-switch';

        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.id = 'scannerModeSwitch';
        checkbox.checked = opts.defaultMode === 'barcode';

        const slider = document.createElement('span');
        slider.className = 'mode-slider';

        switchContainer.appendChild(checkbox);
        switchContainer.appendChild(slider);
        toggleContainer.appendChild(switchContainer);

        // 创建标签显示
        const modeLabel = document.createElement('div');
        modeLabel.className = 'mode-label';

        const icon = document.createElement('i');
        icon.className = opts.defaultMode === 'barcode' ? 'fas fa-barcode' : 'fas fa-qrcode';

        const text = document.createElement('span');
        text.id = 'scannerModeLabel';
        text.textContent = opts.defaultMode === 'barcode' ? ((window.appTexts && window.appTexts.scannerModeGun) || '扫码枪模式') : ((window.appTexts && window.appTexts.scannerModeCamera) || '摄像头模式');

        modeLabel.appendChild(icon);
        modeLabel.appendChild(text);
        toggleContainer.appendChild(modeLabel);

        // 添加到容器
        if (container) {
            container.appendChild(toggleContainer);
        }

        // 绑定事件
        checkbox.addEventListener('change', (e) => {
            const isBarcodeMode = e.target.checked;
            if (typeof opts.onChange === 'function') {
                opts.onChange(isBarcodeMode);
            }
            this.updateModeToggleUI(isBarcodeMode, icon, text);
        });

        return {
            container: toggleContainer,
            checkbox: checkbox,
            icon: icon,
            text: text
        };
    }

    /**
     * 更新模式切换UI
     * @param {boolean} isBarcodeMode - 是否为扫码枪模式
     * @param {HTMLElement} icon - 图标元素
     * @param {HTMLElement} text - 文本元素
     */
    updateModeToggleUI(isBarcodeMode, icon, text) {
        if (isBarcodeMode) {
            icon.className = 'fas fa-barcode';
            text.textContent = ((window.appTexts && window.appTexts.scannerModeGun) || '扫码枪模式');
        } else {
            icon.className = 'fas fa-qrcode';
            text.textContent = ((window.appTexts && window.appTexts.scannerModeCamera) || '摄像头模式');
        }
    }

    /**
     * 创建扫描模式指示器
     * @param {Object} options - 选项
     * @returns {HTMLElement} 指示器元素
     */
    createModeIndicator(options = {}) {
        const defaultOptions = {
            defaultMode: 'barcode',
            onClick: null
        };

        const opts = { ...defaultOptions, ...options };

        const indicator = document.createElement('div');
        indicator.className = 'scanner-mode-indicator';
        indicator.id = 'scannerModeIndicator';
        indicator.title = (window.appTexts && window.appTexts.clickToSwitchScanMode) || '点击切换扫描模式';

        const icon = document.createElement('i');
        icon.className = opts.defaultMode === 'barcode' ? 'fas fa-barcode scanner-mode-icon' : 'fas fa-qrcode scanner-mode-icon';
        icon.id = 'scannerModeIcon';

        const text = document.createElement('span');
        text.className = 'scanner-mode-text';
        text.id = 'scannerModeText';
        text.textContent = opts.defaultMode === 'barcode' ? ((window.appTexts && window.appTexts.scannerModeGun) || '扫码枪模式') : ((window.appTexts && window.appTexts.scannerModeCamera) || '摄像头模式');

        indicator.appendChild(icon);
        indicator.appendChild(text);

        if (typeof opts.onClick === 'function') {
            indicator.addEventListener('click', opts.onClick);
        }

        document.body.appendChild(indicator);
        return indicator;
    }

    /**
     * 更新扫描模式指示器
     * @param {boolean} isBarcodeMode - 是否为扫码枪模式
     */
    updateModeIndicator(isBarcodeMode) {
        const icon = document.getElementById('scannerModeIcon');
        const text = document.getElementById('scannerModeText');

        if (icon && text) {
            if (isBarcodeMode) {
                icon.className = 'fas fa-barcode scanner-mode-icon';
                text.textContent = ((window.appTexts && window.appTexts.scannerModeGun) || '扫码枪模式');
            } else {
                icon.className = 'fas fa-qrcode scanner-mode-icon';
                text.textContent = ((window.appTexts && window.appTexts.scannerModeCamera) || '摄像头模式');
            }
        }
    }

    /**
     * 获取当前扫描状态
     * @returns {Object} 状态信息
     */
    getStatus() {
        return {
            isEnabled: this.isScannerEnabled,
            bufferLength: this.barcodeBuffer.length,
            currentFocusField: this.currentFocusField,
            config: { ...this.config }
        };
    }

    /**
     * 清理资源
     */
    destroy() {
        this.disableScanner();
        
        // 移除创建的DOM元素
        if (this.barcodeInputHidden && this.barcodeInputHidden.parentNode) {
            this.barcodeInputHidden.parentNode.removeChild(this.barcodeInputHidden);
        }
        
        if (this.barcodeInputHint && this.barcodeInputHint.parentNode) {
            this.barcodeInputHint.parentNode.removeChild(this.barcodeInputHint);
        }
        
        // 移除样式
        const styles = document.getElementById('barcode-scanner-styles');
        if (styles) {
            styles.parentNode.removeChild(styles);
        }
        
        // 清理变量
        this.barcodeInputHidden = null;
        this.barcodeInputHint = null;
        this.scannerModeIndicator = null;
        this.barcodeTimer = null;
        this.focusMonitorInterval = null;
    }
}

// 导出到全局作用域
if (typeof window !== 'undefined') {
    window.BarcodeScanner = BarcodeScanner;
}