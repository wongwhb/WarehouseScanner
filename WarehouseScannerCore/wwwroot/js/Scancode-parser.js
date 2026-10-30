/**
 * 高性能条码解析器 - 支持工单、物料、订单条码 + 序号格式条码(118557; 0; 10;0)
 *
 * 设计要点：
 *   - 每次创建新正则实例 → 彻底消除静态共享正则的 lastIndex 竞态
 *   - 原型默认数据 → Object.freeze + Object.assign 降低 GC 压力
 *   - 双策略键值对提取 → 分号优先 + 空格补充，取字段更多者
 *   - 规则链类型识别 → 按优先级遍历，首个命中返回
 *   - 统一结果格式 { success, error, type, data }
 *
 * @example 基础用法
 *   const parser = new BarcodeParser();
 *   parser.parse('PARTNUM:ABC123;QTY:10');
 *
 * @example 扩展别名
 *   parser.addAlias('sku', 'materialCode');
 */
class BarcodeParser {


    /**
     * 默认数据原型 —— 冻结不可变，通过 Object.create(null) + Object.assign 拷贝
     * 避免每次 _getDefaultData() 重新声明字面量带来的属性分配与 GC
     */
    static DEFAULT_DATA_PROTO = Object.freeze({
        workOrderNo: '', assemblySeq: '', materialCode: '', lotNum: '',
        warehouseCode: '', binNum: '', quantity: '', unit: '',
        salesOrder: '', orderLine: '', orderRelNum: '', reserved: ''
    });

    /**
     * 分号分隔键值对正则 —— 每次 new，避免静态共享正则的 lastIndex 竞态
     * 格式: key:value;key:value...
     */
    static _getKvRegex() {
        return /([^：;]+)[：:]([^;]+)(?=;|$)/g;
    }

    /**
     * 空格分隔键值对正则 —— 每次 new，避免静态共享正则的 lastIndex 竞态
     * 格式: PART:xxx PO:xxx QTY:xxx（冒号后无分号，靠"下一个 KEY:"前瞻识别边界）
     */
    static _getSpaceKvRegex() {
        return /([A-Za-z\u4e00-\u9fa5][A-Za-z0-9_\u4e00-\u9fa5]*)\s*[：:]\s*(.*?)(?=\s+[A-Za-z\u4e00-\u9fa5][A-Za-z0-9_\u4e00-\u9fa5]*\s*[：:]|$)/g;
    }

    /** 序号分段字段正则 —— 每次 new */
    static _getSeqHasColonRegex() {
        return /[A-Za-z\u4e00-\u9fa5][A-Za-z0-9_\u4e00-\u9fa5]*[：:]/;
    }

    /** 默认序号索引映射 */
    static DEFAULT_INDEX_MAP = Object.freeze({
        0: 'workOrderNo',
        1: 'assemblySeq',
        2: 'quantity',
        3: 'reserved'
    });

    // ========================================================================
    //  静态工厂：构建默认配置
    // ========================================================================

    /** @returns {Map<string,string>} 别名 → 标准字段名（全部小写键） */
    static _buildDefaultAliases() {
        return new Map([
            // -- 工单号 / PO编号 --
            ['job', 'workOrderNo'],
            ['工单', 'workOrderNo'],
            ['po', 'workOrderNo'],
            ['ponum', 'workOrderNo'],
            ['ponumber', 'workOrderNo'],
            // -- 装配序号 --
            ['asm', 'assemblySeq'],
            ['装配序号', 'assemblySeq'],
            ['assemblyseq', 'assemblySeq'],
            // -- 物料编码 --
            ['partnum', 'materialCode'],
            ['part', 'materialCode'],
            ['物料编号', 'materialCode'],
            ['partnumber', 'materialCode'],
            ['物料', 'materialCode'],
            // 对齐修复：视图层 PART 键检测（PART|物料|物料编号|物料代码）早已把「物料代码」当作
            // 物料键，但解析器别名表此前缺失该键，导致检测命中而解析不识别——补齐使两者一致。
            ['物料代码', 'materialCode', 'PartNum'],
            // -- 批次号 / CTN --
            ['lotnum', 'lotNum', 'LotNum'],
            ['lot', 'lotNum'],
            ['批次号', 'lotNum'],
            ['lotnumber', 'lotNum'],
            ['批号', 'lotNum'],
            ['ctn', 'lotNum'],
            ['ctnnum', 'lotNum'],
            ['carton', 'lotNum'],
            // -- 仓库 --
            ['warehousedesc', 'warehouseCode', 'WarehouseDesc'],
            ['warehouse', 'warehouseCode'],
            ['仓库', 'warehouseCode'],
            ['whsecode', 'warehouseCode'],
            ['warehousecode', 'warehouseCode'],
            ['washouse', 'warehouseCode'],
            ['wh', 'warehouseCode'],
            ['whcode', 'warehouseCode'],
            // -- 库位 --
            ['binnum', 'binNum', 'BinNum'],
            ['bin', 'binNum'],
            ['库位', 'binNum'],
            ['location', 'binNum'],
            ['locationcode', 'binNum'],
            ['binno', 'binNum'],
            // -- 数量 --
            ['qty', 'quantity', 'Qty'],
            ['数量', 'quantity'],
            ['quantity', 'quantity'],
            // -- 单位 --
            ['uom', 'unit', 'UOM'],
            ['单位', 'unit'],
            ['unit', 'unit'],
            // -- 订单号 --
            ['salesorder', 'salesOrder'],
            ['订单号', 'salesOrder'],
            ['ordernum', 'salesOrder'],
            // -- 订单行 --
            ['orderline', 'orderLine'],
            ['订单行', 'orderLine'],
            ['行号', 'orderLine'],
            ['line', 'orderLine'],
            // -- 发货行 --
            ['relnum', 'orderRelNum'],
            ['发货行', 'orderRelNum'],
            ['orderrelnum', 'orderRelNum']
        ]);
    }

    /** @returns {Function[]} 类型识别规则（按优先级排列，返回类型名或 null） */
    static _buildDefaultTypeRules() {
        return Object.freeze([
            data => (data.workOrderNo && data.assemblySeq) ? 'workOrder' : null,
            data => (data.salesOrder && data.orderLine && data.materialCode) ? 'orderWithMaterial' : null,
            data => (data.salesOrder && data.orderLine) ? 'order' : null,
            // 'material' 收紧为必须有物料编码或批次号（纯仓库条码不误判为物料条码），
            //     仅含仓库/库位的条码独立为 'warehouse' 类型，供视图层给出定向提示。
            data => (data.materialCode || data.lotNum) ? 'material' : null,
            data => (data.warehouseCode || data.binNum) ? 'warehouse' : null
        ]);
    }

    // ========================================================================
    //  静态常量：组合条码键组（供视图层模式检测复用，消除逐页手写正则）
    // ========================================================================

    /**
     * PART+PO 组合条码的 PART 语义键。
     * 注意：这是「组合条码检测」专用键组，刻意与别名表物料域保持一致
     * （partnum/partnumber 由别名表覆盖，组合检测沿用既有视图行为不含它们）。
     */
    static PART_KEY_ALIASES = Object.freeze(['part', '物料', '物料编号', '物料代码']);

    /** PO 语义键（别名表中映射到 workOrderNo 的 PO 子集；JOB/工单 不属于此组） */
    static PO_KEY_ALIASES = Object.freeze(['po', 'ponum', 'ponumber']);

    /** JOB 语义键（Outbound 发货计划模式检测用） */
    static JOB_KEY_ALIASES = Object.freeze(['job', '工单']);

    /**
     * 由固定键组构造检测正则（公开静态工具，供视图层替代手写内联正则）
     * 语义与原视图正则一致：键须位于串首或空白/分号之后，冒号前允许空格，大小写不敏感。
     *
     * @param {string[]} keys - 键名列表
     * @returns {RegExp}
     */
    static buildKeyPattern(keys) {
        const escaped = keys.map(k => k.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
        return new RegExp('(?:^|[\\s;])(?:' + escaped.join('|') + ')\\s*[：:]', 'i');
    }

    // ========================================================================
    //  构造函数
    // ========================================================================

    /**
     * @param {Object} [options]
     * @param {boolean} [options.debug=false]         调试日志开关
     * @param {boolean} [options.sequenceEnabled=true] 是否启用序号分段格式解析
     * @param {string}  [options.sequenceSeparator=';'] 序号格式分隔符
     * @param {number}  [options.sequenceMinParts=4]    序号格式最小分段数
     */
    constructor(options = {}) {
        /** @type {boolean} */
        this.debug = !!options.debug;

        /**
         * 字段别名映射（小写键 → 标准字段名）
         * 实例级可变，支持 addAlias / registerPlugin 动态扩展
         * @type {Map<string, string>}
         */
        this._aliasMap = new Map(BarcodeParser._buildDefaultAliases());

        /**
         * 字段值处理器（标准字段名 → 处理函数）
         * 仅在序号分段格式解析时触发，键值对格式保持原始值
         * @type {Object<string, Function>}
         */
        this._processors = Object.create(null);
        this._processors.quantity = (value) => {
            const match = value.match(/^(\d+(?:\.\d+)?)/);
            return match ? match[1] : '';
        };

        /**
         * 类型识别规则链（按数组顺序遍历，首个非 null 返回值为结果）
         * 实例级可变，addTypeRule 会插入头部获得更高优先级
         * @type {Function[]}
         */
        this._typeRules = [...BarcodeParser._buildDefaultTypeRules()];

        /**
         * 序号格式配置
         * @type {{ enabled: boolean, separator: string, minParts: number, indexMap: Object }}
         */
        this.sequenceConfig = {
            enabled: options.sequenceEnabled !== false,
            separator: options.sequenceSeparator || ';',
            minParts: options.sequenceMinParts || 4,
            indexMap: Object.freeze({ ...BarcodeParser.DEFAULT_INDEX_MAP })
        };
    }

    // ========================================================================
    //  公开 API：扩展接口（全部链式调用，返回 this）
    // ========================================================================

    /**
     * 注册单个字段别名
     * @param {string} alias - 原始键名（大小写不敏感）
     * @param {string} field - 目标标准字段名
     * @returns {this}
     */
    addAlias(alias, field) {
        if (alias && field) {
            this._aliasMap.set(alias.toLowerCase(), field);
        }
        return this;
    }

    /**
     * 注册类型识别规则
     * 新规则插入规则链头部，优先级高于全部内置规则。
     *
     * @param {Function} rule - (data) => 类型名或 null
     * @returns {this}
     */
    addTypeRule(rule) {
        if (typeof rule === 'function') {
            this._typeRules.unshift(rule);
        }
        return this;
    }

    /**
     * 把 "362823-3-1" 形式的 PO 值拆成 PONum / OrderLine / OrderRelNum
     * 兼容 "-"、"_"、"/" 分隔；OrderLine/OrderRelNum 必须是数字，PONum 保留原样（Epicor OrderHed.PONum 是 varchar）
     *
     * @param {string} rawPo - PO 原始值
     * @returns {{ ok: boolean, poNum: string, orderLine: number, orderRelNum: number, raw: string }}
     */
    static splitPoRel(rawPo) {
        const out = { ok: false, poNum: '', orderLine: 0, orderRelNum: 0, raw: rawPo || '' };
        if (!rawPo) return out;
        const parts = String(rawPo).trim().split(/[-_\/]/);
        if (parts.length < 3) return out;
        // PONum 保持原样（varchar，可能含字母）；OrderLine / OrderRelNum 必须是数字
        const poNum = parts[0].trim();
        const b = parseInt(parts[1], 10);
        const c = parseInt(parts[2], 10);
        if (!poNum || isNaN(b) || isNaN(c) || b < 0 || c < 0) return out;
        out.poNum = poNum; out.orderLine = b; out.orderRelNum = c; out.ok = true;
        return out;
    }

    /**
     * 检测原始扫码字符串是否为 PART+PO 组合条码
     * 语义与原 Receipt/Outbound 视图实现逐键一致，无行为变化。
     *
     * @param {string} raw - 原始扫码字符串
     * @returns {boolean}
     */
    static isPartPoBarcode(raw) {
        if (!raw) return false;
        return BarcodeParser.buildKeyPattern(BarcodeParser.PART_KEY_ALIASES).test(raw)
            && BarcodeParser.buildKeyPattern(BarcodeParser.PO_KEY_ALIASES).test(raw);
    }

    // ========================================================================
    //  主解析入口
    // ========================================================================

    /**
     * 解析单条条码字符串
     * @param {string} barcode - 原始条码内容
     * @returns {{ success: boolean, error: string, type: string, data: Object }}
     */
    parse(barcode) {
        try {
            if (!barcode || typeof barcode !== 'string') {
                return this._result(false, (window.appTexts && window.appTexts.barcodeEmpty) || '条码内容不能为空');
            }

            // 最小清理（保留空格分隔结构）：用于空格分隔格式解析
            const lightly = this._clean(barcode, { collapseSpaces: true });
            // 深度清理（去冒号/分号两侧空格）：用于分号分隔/序号格式
            const cleaned = this._clean(barcode);
            this._log('轻清理:', lightly, '| 深清理:', cleaned);

            if (!cleaned) {
                return this._result(false, (window.appTexts && window.appTexts.barcodeEmptyAfterClean) || '条码内容为空（清理后）');
            }

            let pairs;
            const hasSemicolon = cleaned.indexOf(';') >= 0;

            if (hasSemicolon) {
                // 含分号：优先分号策略；若提取≤1个则补充空格策略，取字段更多者
                const semicolonPairs = this._extractPairsSemicolon(cleaned);
                const spacePairs = this._extractPairsSpaceSeparated(lightly);
                pairs = spacePairs.length > semicolonPairs.length ? spacePairs : semicolonPairs;
            } else {
                // 无分号：直接走空格分隔策略（PART:xxx PO:xxx QTY:xxx 格式）
                pairs = this._extractPairsSpaceSeparated(lightly);
            }

            if (pairs.length > 0) {
                return this._result(true, '', this._resolvePairs(pairs));
            }

            // 策略3：序号分段格式解析  val1;val2;val3;val4...
            if (this.sequenceConfig.enabled) {
                const seqData = this._parseSequence(cleaned);
                if (seqData) {
                    return this._result(true, '', seqData);
                }
            }

            return this._result(false, (window.appTexts && window.appTexts.noValidFields) || '未识别到有效字段');
        } catch (err) {
            return this._result(false, ((window.appTexts && window.appTexts.parseFailed) || '解析失败：{0}').replace('{0}', err.message));
        }
    }

    /**
     * 解析仓库条码（如 Washouse:01;BinNum:C1-401）
     * 仅当条码包含仓库/库位键时才视为仓库条码，复用 parse() 提取全部字段。
     * 调用方可通过 onParsed 回调取回完整 data 做组合条码回填。
     *
     * @param {string} barcode - 原始条码内容
     * @returns {{ isWarehouseBarcode: boolean, data: Object }}
     *   - isWarehouseBarcode: 是否为仓库条码
     *   - data: 解析后的标准字段对象（含 warehouseCode/binNum 及其他字段）
     */
    parseWarehouseBarcode(barcode) {
        const empty = this._createDefaultData();
        if (!barcode || typeof barcode !== 'string') {
            return { isWarehouseBarcode: false, data: empty };
        }
        const raw = barcode.trim();
        // 检测是否包含仓库键或库位键（大小写不敏感，冒号前允许空格）
        // 检测正则从别名表动态构造（单独手写键清单曾遗漏 whsecode/仓库/库位 等别名），
        // 与解析逻辑保持同一事实来源；addAlias 扩展的别名亦自动生效。
        const hasWarehouseKey = this._buildKeyPatternForFields(['warehouseCode']).test(raw);
        const hasBinKey = this._buildKeyPatternForFields(['binNum']).test(raw);
        if (!hasWarehouseKey && !hasBinKey) {
            return { isWarehouseBarcode: false, data: empty };
        }
        // 复用主解析链提取全部字段（组合条码也能正确拆出 part/po/qty 等）
        const result = this.parse(raw);
        return { isWarehouseBarcode: true, data: result.data };
    }

    /**
     * 从别名表构造键检测正则（B3：单一事实来源，替代手写键清单）
     *
     * @param {string[]} fields - 标准字段名列表（如 ['warehouseCode']、['binNum']）
     * @returns {RegExp} 匹配 "别名:" 的正则（大小写不敏感，冒号前允许空格）
     */
    _buildKeyPatternForFields(fields) {
        const fieldSet = new Set(fields);
        const alternatives = [];
        for (const [alias, field] of this._aliasMap) {
            if (fieldSet.has(field)) {
                // 别名转义，防止特殊字符破坏正则
                alternatives.push(alias.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
            }
        }
        return new RegExp('(?:' + alternatives.join('|') + ')\\s*:', 'i');
    }

    // ========================================================================
    //  内部方法：清理与预处理
    // ========================================================================

    /**
     * 统一清理入口（合并了原 _lightClean / _deepClean 的重复逻辑）
     *
     * @param {string} barcode - 原始条码字符串
     * @param {Object} [options]
     * @param {boolean} [options.collapseSpaces=false]
     *   - true:  连续空格 → 单空格 + trim（空格分隔格式用，保留 KEY:value 分界）
     *   - false: 分隔符两侧空格 → 去除 + trim（分号分隔/序号格式用）
     * @returns {string}
     */
    _clean(barcode, options = {}) {
        const collapseSpaces = options.collapseSpaces === true;
        let s = barcode
            .replace(/　/g, ' ')
            .replace(/[\x00-\x1F\x7F\u200B-\u200F\uFEFF]/g, '')
            .replace(/[：]/g, ':')
            .replace(/[；]/g, ';');
        if (collapseSpaces) {
            s = s.replace(/\s+/g, ' ');
        } else {
            s = s.replace(/\s*([:;])\s*/g, '$1');
        }
        return s.trim();
    }

    /**
     * 分号分隔键值对提取 key:value;key:value...
     * @param {string} cleaned - 深度清理后的条码字符串
     * @returns {Array<{ key: string, value: string }>}
     */
    _extractPairsSemicolon(cleaned) {
        const pairs = [];
        const regex = BarcodeParser._getKvRegex();
        let match;
        while ((match = regex.exec(cleaned)) !== null) {
            const key = match[1].trim();
            if (key) {
                pairs.push({ key, value: match[2].trim() });
            }
        }
        return pairs;
    }

    /**
     * 空格分隔键值对提取 PART:xxx PO:xxx QTY:xxx CTN:xxx ...
     * 使用"下一个 KEY: 前瞻"识别键值对边界，保留键值对之间的空格分隔
     *
     * @param {string} lightly - 最小清理后的条码字符串（保留空格结构）
     * @returns {Array<{ key: string, value: string }>}
     */
    _extractPairsSpaceSeparated(lightly) {
        const pairs = [];
        if (!BarcodeParser._getSeqHasColonRegex().test(lightly)) {
            return pairs;
        }
        const regex = BarcodeParser._getSpaceKvRegex();
        let match;
        while ((match = regex.exec(lightly)) !== null) {
            const key = match[1].trim();
            const value = match[2].trim();
            if (key && value) {
                pairs.push({ key, value });
            }
        }
        return pairs;
    }

    // ========================================================================
    //  内部方法：数据解析
    // ========================================================================

    /**
     * 键值对 → 标准字段映射
     * 特殊处理：
     *   - quantity 字段：提取数字部分 + 抽取单位
     *   - lotNum(CTN) 字段：CTN 号后常跟日期等后缀，只取第一段作为箱号
     *
     * @param {Array<{ key: string, value: string }>} pairs
     * @returns {Object}
     */
    _resolvePairs(pairs) {
        const data = this._createDefaultData();
        for (let i = 0, len = pairs.length; i < len; i++) {
            const { key, value } = pairs[i];
            const field = this._aliasMap.get(key.toLowerCase());
            if (field) {
                if (data[field] !== '') {
                    this._log('重复键覆盖', key, '=', value, '(原值', data[field], ')');
                }
                if (field === 'quantity') {
                    // 提取数字（如 "4EA" → "4"）
                    const processed = this._processors.quantity?.(value) ?? value;
                    data.quantity = processed;
                    // 抽取单位（如 "4EA" → "EA"）
                    if (!data.unit) {
                        const unitMatch = value.match(/[A-Za-z\u4e00-\u9fa5]+$/);
                        if (unitMatch) {
                            data.unit = unitMatch[0].toUpperCase();
                        }
                    }
                } else if (field === 'lotNum') {
                    // CTN:38 08/27/26 → 取第一段"38"作为箱号，丢弃日期后缀
                    const firstSegment = value.split(/\s+/)[0];
                    if (firstSegment) {
                        data.lotNum = firstSegment;
                    } else {
                        data.lotNum = value;
                    }
                } else {
                    data[field] = value;
                }
            }
        }
        return data;
    }

    /**
     * 序号分段格式解析
     * 按分隔符分段后按索引映射字段，并触发字段处理器
     *
     * @param {string} cleaned - 已清理的条码字符串
     * @returns {Object|null} 解析成功返回 data 对象，否则 null
     */
    _parseSequence(cleaned) {
        // 含冒号 → 必然是键值对格式，直接跳过
        if (cleaned.includes(':')) {
            return null;
        }

        const { separator, minParts, indexMap } = this.sequenceConfig;
        const parts = cleaned.split(separator);

        if (parts.length < minParts) {
            return null;
        }

        const data = this._createDefaultData();
        const indexKeys = Object.keys(indexMap);
        for (let i = 0; i < indexKeys.length; i++) {
            const idx = indexKeys[i];
            const field = indexMap[idx];
            const raw = parts[idx] ? parts[idx].trim() : '';
            // 处理器优先，返回 null/undefined 时回退到原始值
            data[field] = this._processors[field]?.(raw) ?? raw;
        }
        return data;
    }

    /**
     * 从冻结原型创建数据对象
     * Object.create(null) 避免原型链查找，Object.assign 拷贝默认值
     *
     * @returns {Object}
     */
    _createDefaultData() {
        return Object.assign(Object.create(null), BarcodeParser.DEFAULT_DATA_PROTO);
    }

    // ========================================================================
    //  内部方法：类型识别与结果构造
    // ========================================================================

    /**
     * 规则链遍历识别条码类型
     * @param {Object} data - 已解析的字段数据
     * @returns {string} 类型标识符
     */
    _identifyType(data) {
        const rules = this._typeRules;
        for (let i = 0; i < rules.length; i++) {
            const type = rules[i](data);
            if (type) return type;
        }
        return 'unknown';
    }

    /**
     * 构建统一返回格式
     * @param {boolean} success
     * @param {string} message
     * @param {Object} [data]
     * @returns {{ success: boolean, error: string, type: string, data: Object }}
     */
    _result(success, message = '', data) {
        const resolved = data || this._createDefaultData();
        return {
            success,
            error: message,
            type: this._identifyType(resolved),
            data: resolved
        };
    }

    /**
     * 调试日志（debug=false 时短路求值，零开销）
     * @param {...*} args
     */
    _log(...args) {
    }

}

// 挂载到全局（兼容现有引用方式）
window.BarcodeParser = BarcodeParser;

/**
 * 扫描输入框 Tab 拦截守卫（全局委托事件）
 * 扫描器以 keyboard wedge 模式发送 Tab 分隔的键值对条码时，
 * Tab 按键会导致焦点跳到下一个输入框，条码被拆成多段散落各字段，
 * 解析器拿不到完整字符串。
 *
 * 守卫策略：当 .scan-input 输入框的当前值已含 "key:" 模式
 * （扫描器刚写入了键值对前两段），拦截 Tab 阻止焦点跳转，
 * 让后续 Tab 分段继续写入同一字段，最终由 Enter 触发完整解析。
 *
 * 手动输入 Tab 场景：值不含冒号 → Tab 正常工作，不影响人工操作。
 */
$(document).on('keydown', '.scan-input', function (e) {
    if (e.key !== 'Tab') return;
    if (!$(this).val().match(/[A-Za-z][A-Za-z0-9]*\s*[：:]/)) return;
    e.preventDefault();
});

/**
 * 仓库条码扫描绑定器（全局工具函数）
 * 在仓库输入框扫码时，将条码信息解析到仓库/库位输入框。
 * 条码格式如：Washouse:01;BinNum:C1-401
 *
 * @param {Object} options
 * @param {string} options.warehouseInput   - 仓库输入框选择器（必填，如 '#warehouseCode'）
 * @param {string} options.locationInput    - 库位输入框选择器（必填，如 '#location'）
 * @param {string} [options.nextFocusInput]  - 解析成功后聚焦的下一个输入框选择器（如 '#batchNo'）
 * @param {boolean} [options.fallbackTab=true] - 非仓库条码时是否走通用 Tab 跳字段逻辑
 * @param {string} [options.fallbackFocus]   - 非仓库条码时直接聚焦的选择器（优先于 fallbackTab）
 * @param {Function} [options.onParsed]      - 解析成功回调 (data) => void，data 含全部字段，可回填组合条码的其他字段
 */
window.bindWarehouseScan = function (options) {
    var whSel = options.warehouseInput;
    var locSel = options.locationInput;
    var nextSel = options.nextFocusInput;
    var fallbackTab = options.fallbackTab !== false;
    var fallbackFocus = options.fallbackFocus;
    var onParsed = options.onParsed;
    var parser = new BarcodeParser();

    $(whSel).on('keydown', function (e) {
        if (e.key !== 'Enter') return;
        var raw = $(whSel).val().trim();
        var result = parser.parseWarehouseBarcode(raw);
        if (result.isWarehouseBarcode) {
            e.preventDefault();
            if (result.data.warehouseCode) $(whSel).val(result.data.warehouseCode);
            if (result.data.binNum) $(locSel).val(result.data.binNum);
            if (onParsed) onParsed(result.data);
            if (nextSel) $(nextSel).focus().select();
        } else if (fallbackFocus) {
            // 非仓库条码：直接跳转到指定输入框
            e.preventDefault();
            $(fallbackFocus).focus().select();
        } else if (fallbackTab) {
            // 非仓库条码：走通用跳字段逻辑
            e.preventDefault();
            var $inputs = $('.input-field:not(.readonly)');
            var idx = $inputs.index(this);
            if (idx >= 0 && idx < $inputs.length - 1) {
                $inputs.eq(idx + 1).focus().select();
            }
        }
    });
};
