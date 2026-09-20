using UnityEngine;

/// <summary>
/// 狙击行为：远距离站桩瞄准 → 红色瞄准线预警（玩家有反应时间找掩体）→ 蓄力结束单发高伤。
/// 关键设计：
/// - 红线实时指向玩家，并用 Raycast 截断：玩家躲进掩体后红线只画到墙上（消失 = 安全）
/// - 蓄力期间玩家丢失视线 → 这一枪取消（打空）
/// - 反应时间（发现→开始瞄准）+ 蓄力时间（红线→开枪）两段延迟，玩家全程有预警
/// </summary>
[BehaviourName("狙击")]
public class EnemySniperBehaviour : EnemyBehaviour
{
    public WeaponData shootWeapon;

    LineRenderer _laser;
    Material _laserMat;

    enum State { Ready, Aiming }
    State _state = State.Ready;
    float _stateUntil;
    float _nextAimAt;   // 冷却结束时间

    static readonly Color LaserColor = new Color(1f, 0.12f, 0.08f, 0.55f);

    protected override void OnInit()
    {
        // 长管枪模型（狙击枪样子）+ 狙击参数武器（完美精度）
        BuildGunModel(new Vector3(0.22f, -0.12f, 0.5f), new Vector3(0.1f, 0.1f, 0.9f));
        shootWeapon = CloneShootWeapon();
        shootWeapon.hipSpread = 0f; shootWeapon.aimSpread = 0f;   // 完美精度（敌人不瞄准，两个都置 0 保持一致）
        ai.shootWeapon = shootWeapon;

        CreateLaser();
    }

    /// <summary>创建瞄准线（默认隐藏，瞄准时显示）</summary>
    void CreateLaser()
    {
        var go = new GameObject("Laser");
        go.transform.SetParent(transform, false);
        _laser = go.AddComponent<LineRenderer>();
        _laser.positionCount = 2;
        _laser.startWidth = 0.03f;
        _laser.endWidth = 0.03f;
        _laserMat = Effects.LineMaterial(LaserColor);
        _laser.sharedMaterial = _laserMat;   // sharedMaterial：不复制材质实例，OnDestroy 销毁模板即可
        _laser.enabled = false;
    }

    public override void Tick(Vector3 target, bool seesPlayer)
    {
        Vector3 to = target - transform.position;
        to.y = 0f;
        float dist = to.magnitude;
        ai.FaceTo(to);

        if (_state == State.Aiming)
        {
            // 蓄力中：红线实时指向玩家（躲进掩体会被截断）
            UpdateLaser(target, seesPlayer);
            if (Time.time >= _stateUntil)
            {
                Fire(target, seesPlayer);
                _state = State.Ready;
                _nextAimAt = Time.time + SniperCooldown();
            }
            return;   // 蓄力中站桩不动
        }

        // Ready：冷却中只走位；冷却完 + 反应时间过 + 看得见 → 进入瞄准
        HideLaser();
        if (Time.time < _nextAimAt)
        {
            KeepDistance(to, dist);
            return;
        }
        if (seesPlayer && ai.ReactionReady)
        {
            _state = State.Aiming;
            _stateUntil = Time.time + SniperAimTime();
            ShowLaser();
        }
        else KeepDistance(to, dist);
    }

    float SniperAimTime() => ai.cfg != null ? ai.cfg.sniperAimTime : 1f;
    float SniperCooldown() => ai.cfg != null ? ai.cfg.sniperCooldown : 2.5f;

    // ---------- 瞄准线 ----------

    void ShowLaser() { if (_laser != null) _laser.enabled = true; }
    public void HideLaser() { if (_laser != null) _laser.enabled = false; }   // public：供 EnemyAI 死亡时隐藏，防止红线冻结在屏幕上

    void UpdateLaser(Vector3 target, bool seesPlayer)
    {
        if (_laser == null || !_laser.enabled) return;

        Vector3 origin = GunTip();
        Vector3 toTarget = target - origin;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.001f) return;

        Vector3 end = origin + toTarget.normalized * Mathf.Min(toTarget.magnitude, ai.sightRange);

        // 被墙/障碍挡住：红线只画到遮挡点（玩家躲进掩体 → 红线消失 = 安全）。
        // 掩码 = Layers.Blocker（只有墙/箱子/地面能截断红线）—— **不含敌人层**：
        // 被"路过挡在中间的另一只敌人"截断不是掩体信号，会让玩家误判"红线变短 = 安全"（那一枪照样打得出来）。
        if (Physics.Raycast(origin, toTarget.normalized, out RaycastHit hit, toTarget.magnitude,
                            Layers.Blocker, QueryTriggerInteraction.Ignore))
        {
            if (hit.transform.root != transform.root) end = hit.point;
        }
        _laser.SetPosition(0, origin);
        _laser.SetPosition(1, end);
    }

    // ---------- 开枪 ----------

    void Fire(Vector3 target, bool seesPlayer)
    {
        HideLaser();
        // 蓄力期间玩家躲进掩体/丢失视线 → 这一枪取消
        if (!seesPlayer) return;

        Vector3 muzzle = GunTip();
        Vector3 dir = target - muzzle;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.001f) return;
        dir.Normalize();

        Bullet.Spawn(muzzle, dir, shootWeapon, Health.Team.Enemy);
        Effects.MuzzleFlash(muzzle, dir);
        Sfx.Shot(shootWeapon);
    }

    /// <summary>枪口位置（枪管前端）</summary>
    Vector3 GunTip()
    {
        return transform.position + transform.forward * 0.9f + Vector3.up * 0.05f;
    }

    void OnDestroy()
    {
        if (_laserMat != null) Destroy(_laserMat);
    }
}