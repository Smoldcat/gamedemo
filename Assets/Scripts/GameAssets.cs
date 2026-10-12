using UnityEngine;

/// <summary>
/// 全局资源总表：把原本「写死在代码里」的外观与音效抽成可替换的资源。
///
/// 用法：本资源放在 Assets/Resources/GameAssets.asset，代码会自动加载，**不需要手动拖引用**。
/// 特性：任何槽位留空 → 该处自动回落到原本的代码生成效果。
///       所以即使你删掉了整个资源表，游戏依然能正常运行，只是用回默认外观/音效。
///
/// 想替换资源时：把你的模型做成 Prefab / 导入音频，拖到对应槽位即可。
/// </summary>
[CreateAssetMenu(fileName = "GameAssets", menuName = "游戏/资源总表", order = 0)]
public class GameAssets : ScriptableObject
{
    // ⚠️ **本类的字段一律不要写 `[Header]`**：分组标题由 `GameAssetsEditor.Fields` 表统一画
    //    （类里再写一遍 = **每组标题重复两行** —— 2026-09-23 修过一次）。
    //    想加/改分组标题 → 改 `GameAssetsEditor` 那张表里的最后一列。
    [Tooltip("敌人的通用身体模型 Prefab（各敌人可单独在 EnemyConfig.bodyPrefab 里覆盖）")]
    public GameObject enemyMeleePrefab;
    [Tooltip("远程敌人手里那把枪的模型 Prefab")]
    public GameObject enemyGunPrefab;

    [Tooltip("血包模型 Prefab")]
    public GameObject healthPackPrefab;

    [Tooltip("命中火花。建议放带 ParticleSystem 的 Prefab，会自动播完即销毁")]
    public GameObject impactPrefab;
    [Tooltip("枪口火光")]
    public GameObject muzzleFlashPrefab;
    [Tooltip("地面弹孔/血迹")]
    public GameObject decalPrefab;

    [Tooltip("伤害飘字 Prefab：含 Text 组件的 UI 物体（可自定义字体/样式/动画）。留空则用代码生成默认文字")]
    public GameObject damageTextPrefab;
    [Tooltip("准星图片（Sprite）。全局默认准星外观；Crosshair 组件上单独拖了则优先用组件的。留空用代码生成的十字")]
    public Sprite crosshairSprite;

    public AudioClip shot;          // 普通枪声（手枪/冲锋枪）
    public AudioClip shotgun;       // 霰弹枪声
    public AudioClip sniper;        // 狙击枪声
    public AudioClip hit;           // 命中反馈
    public AudioClip kill;          // 击杀确认
    public AudioClip pickup;        // 拾取武器
    public AudioClip hurt;          // 受伤
    public AudioClip heal;          // 治疗
    public AudioClip magOut;        // 拔弹匣
    public AudioClip magInChamber;  // 插弹匣上膛
    public AudioClip pump;          // 霰弹枪泵动
    public AudioClip bolt;          // 狙击枪拉栓

    [Tooltip("打开背包")]
    public AudioClip invOpen;
    [Tooltip("关闭背包")]
    public AudioClip invClose;
    [Tooltip("点击/确认（装武器、丢弃确认第一步）")]
    public AudioClip invConfirm;
    [Tooltip("丢弃武器")]
    public AudioClip invDrop;

    // ---------------- 击杀反馈（2026-09-20 新增：屏幕下方中央的「击杀图标 + 名字」） ----------------
    // ⚠️ 新字段一律追加在末尾（见交接文档 §6.8）：Unity 按字段名序列化，追加不会影响已存数值。
    [Tooltip("普通击杀图标（Sprite）。击杀敌人时显示在屏幕下方中央。留空用代码生成的占位（四角星）")]
    public Sprite killIcon;
    [Tooltip("爆头击杀图标（Sprite）。最后致命一击是爆头时用它替换普通图标。留空用代码生成的占位（靶心）")]
    public Sprite killHeadshotIcon;
    [Tooltip("每条击杀反馈的存活时间（秒）：从出现开始计时，到时向右滑出并淡出。默认 2")]
    public float killFeedDuration = 2f;
    [Tooltip("击杀图标大小（px，参考分辨率 1280×720 下的值，运行时按屏幕等比缩放）。默认 64")]
    public float killFeedIconSize = 64f;
    [Tooltip("多个击杀图标之间的间距（px，参考分辨率下）。默认 16")]
    public float killFeedSpacing = 16f;
    [Tooltip("击杀对象名字的字号（参考分辨率下）。默认 20")]
    public int killFeedNameFontSize = 20;
    [Tooltip("同时最多显示几条（0 = 不限，默认 0）")]
    public int killFeedMaxCount = 0;
    [Tooltip("爆头命中反馈音（命中敌人头部时播放，替代普通命中音；不需要击杀就播）")]
    public AudioClip headshotHit;

    // ---------------- 背包（2026-09-20 新增：外观资源槽 + 颜色参数，让背包样式不用改代码） ----------------
    [Tooltip("库存格数（同类武器在同一格里叠加 → 等于「最多能装几种武器」）。默认 20")]
    public int backpackInventorySlots = 20;
    [Tooltip("背包面板底图（Sprite，九宫格）。留空用代码生成的圆角面板。⚠️ 建议白色/浅色 + 透明（颜色由代码控制）")]
    public Sprite backpackPanelSprite;
    [Tooltip("背包描边 / 格子边框（Sprite，九宫格）。用于库存格与丢弃按钮。留空用代码生成")]
    public Sprite backpackFrameSprite;
    [Tooltip("快捷武器槽边框（Sprite，九宫格）。留空用代码生成。⚠️ 建议白色描边 + 透明（当前槽高亮色由代码上）。\n" +
             "⚠️ 名字里是 backpack，但 **HUD 底部快捷栏也在用这一张**（两处共用）→ 换美术时改这一张，背包与快捷栏一起变；\n" +
             "   将来若想给快捷栏单独一套框，需要新加一个字段（别直接改这张，会连带改掉背包）。")]
    public Sprite backpackSlotFrameSprite;
    [Tooltip("血包十字图标（Sprite）。留空用代码生成")]
    public Sprite backpackMedkitIconSprite;
    [Tooltip("背包面板底色（与底图相乘）")]
    public Color backpackPanelColor = new Color(0.04f, 0.05f, 0.09f, 0.92f);
    [Tooltip("快捷槽普通边框色（当前槽改用「当前槽高亮色」）")]
    public Color backpackSlotColor = new Color(0.32f, 0.36f, 0.44f, 0.9f);
    [Tooltip("当前快捷槽的高亮色（背包与 HUD 快捷栏共用）。2026-09-22 由绿改蓝，且改为不透明 —— 绿+4px 描边在深色槽底上看不出\"框住\"")]
    public Color backpackSlotActiveColor = new Color(0.30f, 0.65f, 1f, 1f);
    [Tooltip("库存格子底色")]
    public Color backpackCellColor = new Color(0.16f, 0.18f, 0.26f, 0.92f);
    [Tooltip("背包 UI 整套界面 Prefab（Canvas + 面板结构）。拖入后背包完全用这个 Prefab（可在编辑器里拖拽改布局）；留空用代码生成。⚠️ 节点名有约定，见 BackpackPanel 类注释")]
    public GameObject backpackUIPrefab;

    // ---------------- 字体（2026-09-21 新增：让编辑器与运行时用同一份字体） ----------------
    // ⚠️ 这里**刻意不加 `[Header]` 特性**：GameAssets 的分组标题由 `GameAssetsEditor` 的 Fields 表统一画，
    //    脚本里再写一遍会重复两行（与 2026-09-20 修过的 WeaponData「外观」是同一类问题）。
    [Tooltip("界面统一字体（把 .ttf/.otf 拖进来即可）。\n" +
             "  · 留空 → 回落系统「微软雅黑」→ 再回落引擎内置字体。\n" +
             "  · 填了 → **编辑器与运行时都用它**，于是「编辑期看到的 UI」就是「运行时的样子」（解决「编辑器里用内置占位字体、运行时用系统雅黑 → 两个字体基线不同、文字垂直位置对不上」）。\n" +
             "  · ⚠️ 建议用**开源可商用**字体（如思源黑体 / Noto Sans CJK，OFL）；微软雅黑有版权，不要随工程分发。\n" +
             "  · ⚠️ 换字体会改变 UI 的垂直基线，可能需要微调一次文本的垂直位置。")]
    public Font uiFont;

    // ---------------- 击杀反馈 · 出现 / 消失动画（2026-09-22 新增：从 `KillFeed.cs` 的代码常量搬过来，做成 Inspector 可调） ----------------
    // ⚠️ 物理位置在**末尾**（§6.8「新字段只能在已有字段之后追加」）；但 Inspector 里的分组/顺序由
    //    `GameAssetsEditor.Fields` 决定 → 这几个字段**显示在「击杀反馈」那一组里**，不会跑到最后。
    // 默认值与 `KillFeed.cs` 里的兜底常量**一一对应** → 行为与搬之前完全一致（§4 #10：已存 asset 没这些 key，加载时按代码默认值补齐）。
    [Tooltip("入场「吸到屏幕上」的起始放大倍数（由大 → 小，收束到 1）。越大越有冲击力、越小越含蓄。默认 1.6")]
    [Min(0.01f)] public float killFeedInScale = 1.6f;
    [Tooltip("入场起始透明度（半透明 → 不透明）。0 = 从全透明淡入；1 = 一开始就不透明（只剩缩放）。默认 0.5")]
    [Min(0f)] public float killFeedInAlpha = 0.5f;
    [Tooltip("入场「吸到屏幕上」的时长（秒）。越短越干脆、越长越明显。默认 0.12")]
    [Min(0.01f)] public float killFeedInDuration = 0.12f;
    [Tooltip("消失时长（秒）：存活时间到后「向右滑出 + 淡出」用时。默认 0.3")]
    [Min(0.01f)] public float killFeedOutDuration = 0.3f;
    [Tooltip("消失时向右滑出的距离（px，参考分辨率 1280×720 下，运行时按屏幕等比缩放）。默认 40")]
    [Min(0f)] public float killFeedSlideDistance = 40f;
    [Tooltip("其余条目因重新居中而挪动的速度（px/秒，参考分辨率下）。默认 900")]
    [Min(1f)] public float killFeedRelayoutSpeed = 900f;
    [Tooltip("整组距屏幕底部的距离（px，参考分辨率下）。⚠️ 只在建 Canvas 时读一次 → 运行中改它要重进 Play。默认 150（2026-09-22 由 96 上移：给底部中央的 HUD「快捷栏」腾位）")]
    [Min(0f)] public float killFeedBottomOffset = 150f;

    // ---------------- 虚拟光标（2026-09-22 新增：非游玩态那个自绘指针的外观） ----------------
    // 与 Hotbar / 拾取气泡同一套路：**资源优先 + 代码兜底**（留空就用程序生成的箭头）。
    // ⚠️ 物理位置在**末尾**（§6.8）；Inspector 分组由 `GameAssetsEditor.Fields` 决定。
    // ⚠️ 这两个值**只在建画布时读一次** → 改完要重进 Play（与 `killFeedBottomOffset` 同理）。
    [Tooltip("虚拟光标（打开背包 / 死亡结算时显示的那个指针）的外观图片。\n" +
             "  · 留空 → 用代码生成的白色箭头（白箭头 + 深色描边）。\n" +
             "  · 拖入 → 用你的图。⚠️ 导入设置必须是 **Sprite**（不是 Texture）；若切成多张，取第一张。\n" +
             "  · 【热点 = 指针尖在哪】取 **Sprite 自己的 pivot** —— 在 Sprite Editor 里把 pivot 拖到箭头尖端即可；\n" +
             "    不设（默认 Center）就是「图片中心对准位置」，也完全可用。\n" +
             "  · ⚠️ 只在建画布时读一次 → 换图要重进 Play。")]
    public Sprite virtualCursorSprite;
    [Tooltip("虚拟光标的显示宽度（px，按参考分辨率 1280×720 填，运行时随 UI 等比缩放）。\n" +
             "高度按图片宽高比自动算 → 非正方形的图不会变形。默认 24")]
    [Min(1f)] public float virtualCursorSize = 24f;

    // ---------------- 音效 · 爆炸（2026-09-25 新增） ----------------
    // ⚠️ 物理位置在**末尾**（§6.8「新字段只能在已有字段之后追加」）；Inspector 里仍显示在「音效」分组
    //    （分组与顺序由 GameAssetsEditor.Fields 决定，与类里的物理位置无关）。
    [Tooltip("爆炸音效：RPG 命中与自爆兵自爆共用。留空 = 代码合成的低频爆炸声")]
    public AudioClip explosion;

    // ---------------- HUD 风格（2026-09-25 新增：把 HUD 的信息块统一成「快捷栏 / 背包」那套容器风格） ----------------
    // 做法 = 与快捷栏完全同族：**结构/图片由编辑器工具预置进场景**（菜单 `Tools/游戏/优化 HUD 风格`），
    //   **颜色由代码驱动、集中在本表**（HUD.ApplyHudStyle 每局读一次）→ 想调色只改这里，不必动场景。
    // ⚠️ 物理位置在**末尾**（§6.8）；Inspector 分组由 `GameAssetsEditor.Fields` 决定。
    [Tooltip("HUD 容器化风格的**总开关**：取消勾选 = 代码完全不碰 HUD 的血条模式与各卡片颜色（回到老样子）。\n" +
             "⚠️ 卡片节点由菜单 `Tools/游戏/优化 HUD 风格` 生成并接线；没跑过工具时本开关无效果（没有节点可驱动）。")]
    public bool hudUseNewStyle = true;
    [Tooltip("HUD 卡片底色（左下角状态卡片 / 顶部波次与击杀卡片 / 死亡面板卡片）。默认 = 背包「库存格底色」，保持同族")]
    public Color hudCardColor = new Color(0.16f, 0.18f, 0.26f, 0.92f);
    [Tooltip("HUD 卡片描边色（细圆角描边）。默认 = 快捷槽边框色")]
    public Color hudFrameColor = new Color(0.32f, 0.36f, 0.44f, 0.9f);
    [Tooltip("血条底色（比卡片再深一档，让填充更醒目）")]
    public Color hudBarBgColor = new Color(0.09f, 0.11f, 0.17f, 0.95f);
    [Tooltip("HUD 主文字色（血量数值 / 波次 等）")]
    public Color hudTextColor = new Color(0.90f, 0.93f, 0.98f, 1f);
    [Tooltip("HUD 次要文字色（模式提示 / 空手提示 等）")]
    public Color hudDimTextColor = new Color(0.62f, 0.68f, 0.80f, 1f);
    [Tooltip("低血量警示色：血量低于 35% 时，血条描边会脉动到这个颜色")]
    public Color hudLowHpColor = new Color(1f, 0.25f, 0.2f, 1f);
    [Tooltip("死亡结算面板 · 「重新开始」按钮的文字色（卡片为深色底，用浅色字更清楚）")]
    public Color hudButtonTextColor = new Color(0.95f, 0.97f, 1f, 1f);

    // ---------------- 商店音效（2026-09-25 新增；2026-09-30 补回被旧版文件覆盖掉的这两个字段） ----------------
    // ⚠️ 物理位置在**末尾**（§6.8「新字段只能在已有字段之后追加」）；Inspector 里归「商店音效」分组
    //    （分组与顺序由 GameAssetsEditor.Fields 决定，与类里的物理位置无关）。
    [Tooltip("商店**买入成功**的音效（收银机「叮」）。留空 = 代码合成的占位音")]
    public AudioClip shopBuy;
    [Tooltip("商店**卖出成功**的音效（金币入袋「叮当」）。留空 = 代码合成的占位音")]
    public AudioClip shopSell;

    // ---------------- 投掷物选择器（2026-10-12 新增） ----------------
    // ⚠️ 物理位置在**末尾**（§6.8「新字段只能在已有字段之后追加」）；Inspector 里归「投掷物选择器」分组
    //    （分组与顺序由 `GameAssetsEditor.Fields` 决定，与类里的物理位置无关）。
    [Tooltip("选择器「无操作自动关」的秒数：在 G 槽上再按一次 G 打开选择器后，这么久没操作就自动收起。\n" +
             "（排序规则 / 到底绕回这类**语义**不在这里，见 PlayerThrowable 的常量——它们不是调手感的东西）")]
    [Min(0.1f)] public float throwableSelectorTimeout = 1.5f;
    [Tooltip("选择器每行高度（px，按参考分辨率 1280×720 填，运行时随 UI 等比缩放）")]
    [Min(16f)] public float throwableListRowH = 24f;
    [Tooltip("选择器列表文字字号（参考分辨率下）")]
    [Min(8)] public int throwableListFontSize = 15;
    [Tooltip("选择器列表宽度（px，参考分辨率下）")]
    [Min(80f)] public float throwableListWidth = 230f;
    [Tooltip("选择器列表 · 普通行底色")]
    public Color throwableListBgColor = new Color(0f, 0f, 0f, 0.55f);
    [Tooltip("选择器列表 · **当前选中行**的高亮色")]
    public Color throwableListHiColor = new Color(1f, 0.85f, 0.35f, 0.9f);
    [Tooltip("投掷物 · **范围指示圈**的环颜色（含透明度；运行中实时生效）—— 由「效果卡勾了显示范围圈」的投掷物在按住右键预览时使用")]
    public Color throwableRangeRingColor = new Color(1f, 0.55f, 0.1f, 0.95f);
    [Tooltip("投掷物 · **范围指示圈**的填充颜色（含透明度；默认 10% 橙 = 地面「提亮变暖」、一眼看出危险区，调大只是更实）")]
    public Color throwableRangeFillColor = new Color(1f, 0.55f, 0.1f, 0.10f);

    // ---------------- 切枪动作（2026-10-12 新增） ----------------
    // ⚠️ 物理位置在**末尾**（§6.8「新字段只能在已有字段之后追加」）；Inspector 里归「切枪动作」分组
    //    （分组与顺序由 `GameAssetsEditor.Fields` 决定，与类里的物理位置无关）。
    [Tooltip("切枪动作时长（秒）：**从按下切枪键那一刻起、到「新枪可以开火」之间的时间** —— 收枪段 + 出枪段都算，\n" +
             "这段时间内不能开火、不能起手换弹 / 按 T 换级（换手 = 手上要动一次）。\n" +
             "以后做了切枪动画：把这个数**填成动画的实际长度**即可（动作 = 先放下旧枪、再拿起新枪）。\n" +
             "⚠️ 填 0 = 关闭（退回「切完枪立刻能开火」）。默认 0.3")]
    [Min(0f)] public float switchWindup = 0.3f;

    const string ResourcePath = "GameAssets";

    static GameAssets _instance;
    static bool _loaded;

    /// <summary>自动从 Resources 加载；没放资源表时返回 null，调用方需按「留空」处理</summary>
    public static GameAssets I
    {
        get
        {
            if (!_loaded)
            {
                _loaded = true;
                _instance = Resources.Load<GameAssets>(ResourcePath);
            }
            return _instance;
        }
    }

    /// <summary>静态状态清理（供 StaticReset 调用）：关闭 Reload Domain 时强制下次访问重新加载</summary>
    internal static void ResetStatics()
    {
        _instance = null;
        _loaded = false;
    }
}
