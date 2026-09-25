using UnityEngine;

/// 程序化合成音效（无需任何音频资源）：枪声、爆炸、命中、拾取、受伤、治疗、分武器换弹
public static class Sfx
{
    const int SR = 44100;
    static AudioSource _src;
    static bool _clipsReady;
    static AudioClip _shot, _shotgun, _sniper, _hit, _kill, _pickup, _hurt, _heal;
    static AudioClip _magOut, _magInChamber, _pump, _bolt;   // 换弹音效组
    static AudioClip _invOpen, _invClose, _confirm, _drop;   // 背包 UI 音效组
    static AudioClip _shopBuy, _shopSell;                     // 商店买卖音效组（2026-09-25）
    static AudioClip _headshotHit;                            // 爆头命中反馈
    static AudioClip _explosion;                              // 爆炸（RPG 命中 / 自爆兵自爆共用）

    /// <summary>静态状态清理（供 StaticReset 调用）。
    /// 合成的 AudioClip 与 AudioSource 在退出 Play 时会被销毁，而 _clipsReady 若还是 true，
    /// Init() 就再也不会重建它们 → 表现为「进下一局后所有音效消失」。必须整体复位重来。</summary>
    internal static void ResetStatics()
    {
        // ⚠️ 这里**故意不销毁 AudioClip**：`Use(custom, fallback)` 会优先用 GameAssets 里配的音频，
        // 那是**工程资产**，销毁等于删资源；而合成出来的 clip 会在退出 Play 时由引擎统一销毁，
        // 且 _clipsReady=false 已保证下次重建 → 不需要（也不应该）手动释放。
        _src = null;
        _clipsReady = false;
        _shot = _shotgun = _sniper = _hit = _kill = _pickup = _hurt = _heal = null;
        _headshotHit = null;
        _magOut = _magInChamber = _pump = _bolt = null;
        _invOpen = _invClose = _confirm = _drop = null;
        _shopBuy = _shopSell = null;
        _explosion = null;
    }

    /// <summary>
    /// 初始化音频。每局开始时由 WaveManager 调用。
    /// 音频源是场景物体，重载场景后会被销毁，所以要检查重建；
    /// 合成音效只生成一次，避免每局都留下没人引用的旧 AudioClip。
    /// </summary>
    public static void Init()
    {
        if (_src == null)
        {
            var go = new GameObject("SfxSource");
            _src = go.AddComponent<AudioSource>();
            _src.spatialBlend = 0f;   // 2D 音效，不做空间衰减
            _src.volume = 0.6f;
        }

        if (_clipsReady) return;
        _clipsReady = true;

        _shot = Make("shot", 0.11f, t =>
        {
            float env = Mathf.Exp(-t * 38f);
            float noise = (Random.value * 2f - 1f);
            float thump = Mathf.Sin(2f * Mathf.PI * 110f * t) * 0.7f;
            return (noise * 0.8f + thump) * env * 0.7f;
        });
        _shotgun = Make("shotgun", 0.2f, t =>
        {
            float env = Mathf.Exp(-t * 20f);
            float noise = (Random.value * 2f - 1f);
            float thump = Mathf.Sin(2f * Mathf.PI * 70f * t) * 0.9f;
            return (noise * 0.9f + thump) * env * 0.9f;
        });
        _sniper = Make("sniper", 0.32f, t =>
        {
            float env = Mathf.Exp(-t * 13f);                                                    // 沉重的低频轰鸣
            float noise = (Random.value * 2f - 1f);
            float boom = Mathf.Sin(2f * Mathf.PI * 58f * t) * 1.1f;
            float crack = Mathf.Sin(2f * Mathf.PI * 1500f * t) * Mathf.Exp(-t * 90f) * 0.5f;   // 出膛高频爆响
            return (noise * 0.7f + boom + crack) * env * 0.85f;
        });
        _hit = Make("hit", 0.06f, t =>
        {
            float env = Mathf.Exp(-t * 60f);
            return Mathf.Sin(2f * Mathf.PI * 340f * t) * env * 0.5f;
        });
        // 爆头命中：比普通命中更脆更亮（高频爆响 + 一点低频冲击），一听就能和普通命中区分开
        _headshotHit = Make("headshotHit", 0.10f, t =>
        {
            float env = Mathf.Exp(-t * 42f);
            float crack = Mathf.Sin(2f * Mathf.PI * 980f * t) * Mathf.Exp(-t * 55f);
            float thump = Mathf.Sin(2f * Mathf.PI * 230f * t) * 0.5f;
            return (crack + thump) * env * 0.7f;
        });
        // 击杀确认：低频重击 + 高频清脆（比命中更有"打中要害"的感觉）
        _kill = Make("kill", 0.18f, t =>
        {
            float env = Mathf.Exp(-t * 24f);
            float thump = Mathf.Sin(2f * Mathf.PI * 160f * t) * Mathf.Exp(-t * 18f);
            float click = Mathf.Sin(2f * Mathf.PI * 1200f * t) * Mathf.Exp(-t * 90f) * 0.4f;
            return (thump * 0.9f + click) * env;
        });
        _pickup = Make("pickup", 0.16f, t =>
        {
            float f = Mathf.Lerp(420f, 980f, t / 0.16f);
            float env = Mathf.Exp(-t * 12f);
            return Mathf.Sin(2f * Mathf.PI * f * t) * env * 0.4f;
        });
        _hurt = Make("hurt", 0.14f, t =>
        {
            float env = Mathf.Exp(-t * 26f);
            return Mathf.Sin(2f * Mathf.PI * 180f * t) * env * 0.55f;
        });
        _heal = Make("heal", 0.22f, t =>
        {
            float f = t < 0.09f ? 523f : 784f;   // C5 → G5 上行双音
            float env = Mathf.Exp(-t * 10f);
            return Mathf.Sin(2f * Mathf.PI * f * t) * env * 0.42f;
        });

        // ---- 换弹音效组 ----
        // 拔弹匣：短促金属摩擦 + 弹匣释放"嗒"
        _magOut = Make("magOut", 0.09f, t =>
        {
            float env = Mathf.Exp(-t * 70f);
            float noise = (Random.value * 2f - 1f);
            float click = Mathf.Sin(2f * Mathf.PI * 520f * t) * 0.35f;
            return (noise * 0.55f + click) * env * 0.4f;
        });
        // 插弹匣 + 上膛：先闷响入匣（0~0.12s），后套筒双击（0.14s 起）
        _magInChamber = Make("magInChamber", 0.28f, t =>
        {
            float envIn = Mathf.Exp(-t * 55f);
            float magIn = (t < 0.12f)
                ? ((Random.value * 2f - 1f) * 0.5f + Mathf.Sin(2f * Mathf.PI * 140f * t) * 0.7f) * envIn * 0.5f
                : 0f;
            float tc = Mathf.Max(0f, t - 0.14f);
            float envC = Mathf.Exp(-tc * 75f);
            float chamber = (t >= 0.14f)
                ? ((Random.value * 2f - 1f) * 0.45f + Mathf.Sin(2f * Mathf.PI * 950f * t) * 0.3f) * envC * 0.5f
                : 0f;
            return magIn + chamber;
        });
        // 霰弹枪泵动：护木"吱-咔"两段（后拉 0~0.10s，前推 0.10s 起）
        _pump = Make("pump", 0.24f, t =>
        {
            float t2 = Mathf.Max(0f, t - 0.10f);
            float env1 = Mathf.Exp(-t * 45f);
            float env2 = Mathf.Exp(-t2 * 45f);
            float noise = (Random.value * 2f - 1f);
            float thunk1 = (noise * 0.5f + Mathf.Sin(2f * Mathf.PI * 180f * t) * 0.8f) * env1;
            float thunk2 = (t > 0.10f)
                ? (noise * 0.5f + Mathf.Sin(2f * Mathf.PI * 220f * t) * 0.8f) * env2 : 0f;
            return (thunk1 + thunk2) * 0.55f;
        });
        // 狙击枪拉栓：两声沉重金属撞击（0~0.08s 开栓，0.08s 起推栓）带余音
        _bolt = Make("bolt", 0.20f, t =>
        {
            float t2 = Mathf.Max(0f, t - 0.08f);
            float env1 = Mathf.Exp(-t * 60f);
            float env2 = Mathf.Exp(-t2 * 60f);
            float ring = Mathf.Sin(2f * Mathf.PI * 1200f * t) * 0.22f;
            float clack1 = ((Random.value * 2f - 1f) * 0.5f + Mathf.Sin(2f * Mathf.PI * 300f * t) * 0.7f) * env1;
            float clack2 = (t > 0.08f)
                ? ((Random.value * 2f - 1f) * 0.5f + Mathf.Sin(2f * Mathf.PI * 260f * t) * 0.7f + ring * env2) * env2
                : 0f;
            return (clack1 + clack2) * 0.6f;
        });
        // 爆炸：低频冲击波 + 高频爆裂 + 噪声碎裂（RPG 命中与自爆兵自爆共用；资源表配了「爆炸」则优先用它）
        _explosion = Make("explosion", 0.5f, t =>
        {
            float env = Mathf.Exp(-t * 7f);
            float noise = (Random.value * 2f - 1f);
            float boom = Mathf.Sin(2f * Mathf.PI * 46f * t) * 1.2f;
            float crack = Mathf.Sin(2f * Mathf.PI * 320f * t) * Mathf.Exp(-t * 35f) * 0.6f;
            return (noise * 0.8f + boom + crack) * env * 0.9f;
        });

        // ---- 背包 UI 音效组 ----
        // 打开：短促上滑"唰"（带一点布/皮革感噪声）
        _invOpen = Make("invOpen", 0.12f, t =>
        {
            float f = Mathf.Lerp(320f, 760f, t / 0.12f);
            float env = Mathf.Exp(-t * 16f);
            float tone = Mathf.Sin(2f * Mathf.PI * f * t);
            float noise = (Random.value * 2f - 1f) * 0.5f;
            return (tone + noise) * env * 0.45f;
        });
        // 关闭：下滑"唰"（与打开方向相反）
        _invClose = Make("invClose", 0.12f, t =>
        {
            float f = Mathf.Lerp(760f, 320f, t / 0.12f);
            float env = Mathf.Exp(-t * 16f);
            float tone = Mathf.Sin(2f * Mathf.PI * f * t);
            float noise = (Random.value * 2f - 1f) * 0.5f;
            return (tone + noise) * env * 0.42f;
        });
        // 确认：短促"嗒"（点按钮/装武器）
        _confirm = Make("confirm", 0.07f, t =>
        {
            float env = Mathf.Exp(-t * 70f);
            float click = Mathf.Sin(2f * Mathf.PI * 620f * t) * 0.5f;
            return click * env * 0.5f;
        });
        // 丢弃：短促下坠"咚"（低频 + 噪声）
        _drop = Make("drop", 0.13f, t =>
        {
            float env = Mathf.Exp(-t * 30f);
            float thump = Mathf.Sin(2f * Mathf.PI * 130f * t) * 0.8f;
            float noise = (Random.value * 2f - 1f) * 0.4f;
            return (thump + noise) * env * 0.5f;
        });

        // ---- 商店音效组（2026-09-25）----
        // 买入：收银机"叮"（两段上行清脆音：第一声确认、第二声上挑，像"成交"）
        _shopBuy = Make("shopBuy", 0.18f, t =>
        {
            float f = t < 0.07f ? 1046.5f : 1568f;        // C6 → G6
            float env = Mathf.Exp(-t * 14f);
            float tone = Mathf.Sin(2f * Mathf.PI * f * t);
            float click = (t < 0.02f) ? (Random.value * 2f - 1f) * 0.3f * Mathf.Exp(-t * 120f) : 0f;
            return (tone * 0.5f + click) * env * 0.6f;
        });
        // 卖出：金币入袋"叮当"（两个短促的金属高频音，第二声更高更轻）
        _shopSell = Make("shopSell", 0.16f, t =>
        {
            float env1 = Mathf.Exp(-t * 55f);
            float t2 = Mathf.Max(0f, t - 0.06f);
            float env2 = Mathf.Exp(-t2 * 55f);
            float ping1 = Mathf.Sin(2f * Mathf.PI * 1568f * t) * env1;
            float ping2 = (t >= 0.06f) ? Mathf.Sin(2f * Mathf.PI * 2093f * t) * env2 * 0.8f : 0f;
            return (ping1 + ping2) * 0.45f;
        });
    }

    static AudioClip Make(string name, float dur, System.Func<float, float> gen)
    {
        int n = (int)(dur * SR);
        var data = new float[n];
        for (int i = 0; i < n; i++) data[i] = gen(i / (float)SR);
        var clip = AudioClip.Create(name, n, 1, SR, false);
        clip.SetData(data, 0);
        return clip;
    }

    public static void Play(AudioClip clip, float vol = 1f)
    {
        if (_src == null || clip == null) return;
        _src.PlayOneShot(clip, vol);
    }

    /// <summary>优先用资源表里的真实音频文件；没配则回落到代码合成的默认音效</summary>
    static AudioClip Use(AudioClip custom, AudioClip fallback)
    {
        return custom != null ? custom : fallback;
    }

    public static void Shot(WeaponData w)
    {
        // 每把枪专属的开火音效优先（配了就用；敌人手里的克隆枪经 Clone() 自动继承）
        if (w != null && w.fireSound != null) { Play(w.fireSound); return; }
        var ga = GameAssets.I;
        switch (w.sound)
        {
            case WeaponData.SoundKind.Shotgun: Play(Use(ga != null ? ga.shotgun : null, _shotgun)); break;
            case WeaponData.SoundKind.Sniper: Play(Use(ga != null ? ga.sniper : null, _sniper)); break;
            default: Play(Use(ga != null ? ga.shot : null, _shot)); break;
        }
    }

    /// 换弹开始：手枪/冲锋枪拔弹匣，霰弹枪拉护木，狙击枪开栓
    public static void ReloadStart(WeaponData w)
    {
        // ① 每把枪专属的换弹音效优先（配了就用，按它自己的音量放 —— 与 `Shot` 的专属开火音同一套约定）
        if (w != null && w.reloadStartSound != null) { Play(w.reloadStartSound); return; }
        // ② 留空 → 按「枪声类型」分组回落全局槽（手枪/冲锋枪 = 拔弹匣、霰弹 = 拉护木、狙击 = 开栓）
        var ga = GameAssets.I;
        switch (w.sound)
        {
            case WeaponData.SoundKind.Shotgun: Play(Use(ga != null ? ga.pump : null, _pump), 0.9f); break;
            case WeaponData.SoundKind.Sniper: Play(Use(ga != null ? ga.bolt : null, _bolt), 0.9f); break;
            default: Play(Use(ga != null ? ga.magOut : null, _magOut)); break;
        }
    }

    /// 换弹完成：手枪/冲锋枪插匣上膛，霰弹枪再拉护木上膛，狙击枪推栓
    public static void ReloadEnd(WeaponData w)
    {
        // ① 每把枪专属的换弹完成音效优先（留空则与下面完全一致 —— 8 把枪现在都留空）
        if (w != null && w.reloadEndSound != null) { Play(w.reloadEndSound); return; }
        // ② 留空 → 按「枪声类型」分组回落全局槽（插匣上膛 / 护木回推 / 推栓）
        var ga = GameAssets.I;
        switch (w.sound)
        {
            case WeaponData.SoundKind.Shotgun: Play(Use(ga != null ? ga.pump : null, _pump), 0.95f); break;
            case WeaponData.SoundKind.Sniper: Play(Use(ga != null ? ga.bolt : null, _bolt), 0.95f); break;
            default: Play(Use(ga != null ? ga.magInChamber : null, _magInChamber), 0.95f); break;
        }
    }

    public static void Hit() { var ga = GameAssets.I; Play(Use(ga != null ? ga.hit : null, _hit)); }
    /// <summary>爆头命中反馈：优先用资源表 headshotHit，留空回落代码合成（命中头部即播，不必击杀）</summary>
    public static void HeadshotHit() { var ga = GameAssets.I; Play(Use(ga != null ? ga.headshotHit : null, _headshotHit)); }
    public static void Kill() { var ga = GameAssets.I; Play(Use(ga != null ? ga.kill : null, _kill), 0.7f); }
    public static void Pickup() { var ga = GameAssets.I; Play(Use(ga != null ? ga.pickup : null, _pickup)); }
    public static void Hurt() { var ga = GameAssets.I; Play(Use(ga != null ? ga.hurt : null, _hurt)); }
    public static void Heal() { var ga = GameAssets.I; Play(Use(ga != null ? ga.heal : null, _heal)); }
    /// <summary>爆炸：RPG 命中与自爆兵自爆共用。优先用资源表 explosion，留空回落代码合成</summary>
    public static void Explosion() { var ga = GameAssets.I; Play(Use(ga != null ? ga.explosion : null, _explosion), 0.9f); }

    // ---- 背包 UI 音效：优先用资源表里的真实音频，没配则回落代码合成 ----
    public static void OpenInventory() { var ga = GameAssets.I; Play(Use(ga != null ? ga.invOpen : null, _invOpen), 0.7f); }
    public static void CloseInventory() { var ga = GameAssets.I; Play(Use(ga != null ? ga.invClose : null, _invClose), 0.7f); }
    public static void Confirm() { var ga = GameAssets.I; Play(Use(ga != null ? ga.invConfirm : null, _confirm), 0.8f); }
    public static void Drop() { var ga = GameAssets.I; Play(Use(ga != null ? ga.invDrop : null, _drop), 0.8f); }

    // ---- 商店买卖：优先用资源表里的真实音频，没配则回落代码合成 ----
    public static void Buy() { var ga = GameAssets.I; Play(Use(ga != null ? ga.shopBuy : null, _shopBuy), 0.8f); }
    public static void Sell() { var ga = GameAssets.I; Play(Use(ga != null ? ga.shopSell : null, _shopSell), 0.8f); }
}
