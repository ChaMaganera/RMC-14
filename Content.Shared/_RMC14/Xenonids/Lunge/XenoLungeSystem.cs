using System.Linq;
using System.Numerics;
using Content.Shared._RMC14.Barricade;
using Content.Shared._RMC14.Barricade.Components;
using Content.Shared._RMC14.Damage.ObstacleSlamming;
using Content.Shared._RMC14.Entrenching;
using Content.Shared._RMC14.Movement;
using Content.Shared._RMC14.Pulling;
using Content.Shared._RMC14.Stun;
using Content.Shared._RMC14.Weapons.Melee;
using Content.Shared._RMC14.Xenonids.Leap;
using Content.Shared.Mobs.Systems;
using Content.Shared.IdentityManagement;
using Content.Shared.Movement.Pulling.Events;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.StatusEffect;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;


namespace Content.Shared._RMC14.Xenonids.Lunge;

public sealed class XenoLungeSystem : EntitySystem
{
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ThrownItemSystem _thrownItem = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly XenoSystem _xeno = default!;
    [Dependency] private readonly RMCPullingSystem _rmcPulling = default!;
    [Dependency] private readonly SharedRMCLagCompensationSystem _rmcLagCompensation = default!;
    [Dependency] private readonly RMCObstacleSlammingSystem _rmcObstacleSlamming = default!;
    [Dependency] private readonly XenoLeapSystem _leap = default!;
    [Dependency] private readonly RMCSizeStunSystem _size = default!;

    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<ThrownItemComponent> _thrownItemQuery;

    public override void Initialize()
    {
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _thrownItemQuery = GetEntityQuery<ThrownItemComponent>();

        SubscribeAllEvent<XenoLungePredictedHitEvent>(OnPredictedHit);

        SubscribeLocalEvent<XenoLungeComponent, XenoLungeActionEvent>(OnXenoLungeAction);
        SubscribeLocalEvent<XenoLungeComponent, MeleeAttackAttemptEvent>(OnAttackAttempt);

        SubscribeLocalEvent<XenoActiveLungeComponent, ThrowDoHitEvent>(OnXenoLungeHit);
        SubscribeLocalEvent<XenoActiveLungeComponent, LandEvent>(OnXenoLungeLand);

        SubscribeLocalEvent<RMCLungeProtectionComponent, XenoLungeHitAttempt>(OnXenoLungeHitAttempt);

        SubscribeLocalEvent<XenoLungeStunnedComponent, PullStoppedMessage>(OnXenoLungeStunnedPullStopped);
    }

    private void OnPredictedHit(XenoLungePredictedHitEvent msg, EntitySessionEventArgs args)
    {
        if (_net.IsClient)
            return;

        if (args.SenderSession.AttachedEntity is not { } ent)
            return;

        if (!TryComp(ent, out XenoActiveLungeComponent? lunging))
            return;

        if (GetEntity(msg.Target) is not { Valid: true } target)
            return;

        if (!lunging.Running)
            return;

        if (lunging.Target != target)
            return;

        _rmcLagCompensation.SetLastRealTick(args.SenderSession.UserId, msg.LastRealTick);
        ApplyLungeHitEffects((ent, lunging), target, true, false);
    }

    private void OnXenoLungeAction(Entity<XenoLungeComponent> xeno, ref XenoLungeActionEvent args)
    {
        if (args.Entity is not { } target)
            return;

        if (!_xeno.CanAbilityAttackTarget(xeno, target))
            return;

        if (args.Handled)
            return;

        var attempt = new XenoLungeAttemptEvent();
        RaiseLocalEvent(xeno, ref attempt);

        if (attempt.Cancelled)
            return;

        args.Handled = true;

        _rmcPulling.TryStopAllPullsFromAndOn(xeno);

        var origin = _transform.GetMapCoordinates(xeno);
        var targetCoords = _rmcLagCompensation.GetCoordinates(target, xeno);
        var targetMapCoordinates = _transform.ToMapCoordinates(targetCoords);
        var active = EnsureComp<XenoActiveLungeComponent>(xeno);
        var diff = targetMapCoordinates.Position - origin.Position;
        var targetDistance = diff.Length();
        var direction = diff.Normalized();
        var nearestBarbedDistance = float.MaxValue;
        Vector2? nearestBarbedHitPosition = null;
        diff = direction * xeno.Comp.Range;

        if (_net.IsServer)
        {
            var results = _physics.IntersectRay(
                origin.MapId,
                new CollisionRay(origin.Position, direction, (int) CollisionGroup.BarbedBarricade),
                diff.Length(),
                xeno.Owner,
                false
            );
            foreach (var result in results)
            {
                if (result.Distance >= nearestBarbedDistance ||
                    !HasComp<BarricadeComponent>(result.HitEntity) ||
                    !TryComp<BarbedComponent>(result.HitEntity, out var barbed) ||
                    !barbed.IsBarbed)
                    continue;
                nearestBarbedDistance = result.Distance;
                nearestBarbedHitPosition = result.HitPos;
            }
            if (nearestBarbedHitPosition != null)
            {
                diff = nearestBarbedHitPosition.Value - origin.Position - direction * 0.5f;
                if (nearestBarbedDistance <= 0.5f)
                    diff = Vector2.Zero;
            }
        }

        active.Origin = origin;
        active.Charge = diff;
        active.Target = target;
        active.TargetCoordinates = targetMapCoordinates;
        active.Range = xeno.Comp.Range;
        active.StunTime = xeno.Comp.StunTime;
        active.TargetDistance = targetDistance;
        active.NearestBarbedDistance = nearestBarbedDistance;
        Dirty(xeno);

        _rmcObstacleSlamming.MakeImmune(xeno, 0.5f);
        _throwing.TryThrow(xeno, diff, 30, animated: false);

        if (!_physicsQuery.TryGetComponent(xeno, out var physics))
            return;

        // Handle close-range or same-tile lunges
        foreach (var ent in _physics.GetContactingEntities(xeno.Owner, physics))
        {
            if (ent != target)
                continue;

            if (ApplyLungeHitEffects(xeno.Owner, ent, true))
                return;
        }
    }

    private void OnAttackAttempt(Entity<XenoLungeComponent> ent, ref MeleeAttackAttemptEvent args)
    {
        var netAttacker = GetNetEntity(ent);
        if (!TryComp(GetEntity(args.Target), out XenoLungeStunnedComponent? stunned) ||
            netAttacker != stunned.Stunner)
        {
            return;
        }

        switch (args.Attack)
        {
            case DisarmAttackEvent disarm:
                args.Attack = new LightAttackEvent(disarm.Target, netAttacker, disarm.Coordinates);
                break;
        }
    }

    private void OnXenoLungeHit(Entity<XenoActiveLungeComponent> xeno, ref ThrowDoHitEvent args)
    {
        if (!_mobState.IsAlive(xeno) || HasComp<StunnedComponent>(xeno))
        {
            RemCompDeferred<XenoActiveLungeComponent>(xeno);
            return;
        }

        ApplyLungeHitEffects(xeno.AsNullable(), args.Target, true);
    }

    private void OnXenoLungeLand(Entity<XenoActiveLungeComponent> ent, ref LandEvent args)
    {
        if (!_pulling.IsPulling(ent))
            ApplyLungeHitEffects(ent.AsNullable(), ent.Comp.Target, false);

        RemCompDeferred<XenoActiveLungeComponent>(ent);
    }

    private bool ApplyLungeHitEffects(Entity<XenoActiveLungeComponent?> xeno, EntityUid targetId, bool stopThrow, bool predicted = true)
    {
        if (!Resolve(xeno, ref xeno.Comp, false))
            return false;

        if (_mobState.IsDead(targetId))
            return false;

        if (HasComp<BarricadeComponent>(targetId) &&
            TryComp<BarbedComponent>(targetId, out var barbed) &&
            barbed.IsBarbed)
        {
            HandleBarbedBarricadeHit((xeno.Owner, xeno.Comp), targetId);
            return true;
        }

        if (_physicsQuery.TryGetComponent(xeno, out var physics) &&
            _thrownItemQuery.TryGetComponent(xeno, out var thrown))
        {
            _thrownItem.LandComponent(xeno, thrown, physics, true);

            if (stopThrow)
                _thrownItem.StopThrow(xeno, thrown);
        }

        var ev = new XenoLungeHitAttempt(xeno);
        RaiseLocalEvent(targetId, ref ev);

        if (ev.Cancelled)
        {
            StopLunge(xeno);
            return true;
        }

        if (!_xeno.CanAbilityAttackTarget(xeno, targetId) ||
            (_size.TryGetSize(targetId, out var size) && size >= RMCSizes.Big) ||
            (TryComp<XenoComponent>(targetId, out var xenoComp) && xenoComp.Tier >= 2)) //Fails if big or tier 2 or more
        {
            return true;
        }

        if (_net.IsServer)
        {
            var stunTime = _xeno.TryApplyXenoDebuffMultiplier(targetId, xeno.Comp.StunTime);
            _stun.TryParalyze(targetId, stunTime, true);

            var stunned = EnsureComp<XenoLungeStunnedComponent>(targetId);
            stunned.ExpireAt = _timing.CurTime + stunTime;
            stunned.Stunner = GetNetEntity(xeno);
            Dirty(targetId, stunned);
        }

        if (TryComp(xeno, out MeleeWeaponComponent? melee))
        {
            melee.NextAttack = _timing.CurTime;
            Dirty(xeno, melee);
        }

        if (_net.IsClient && predicted)
        {
            var predictedEv = new XenoLungePredictedHitEvent(GetNetEntity(targetId), _rmcLagCompensation.GetLastRealTick(null));
            RaiseNetworkEvent(predictedEv);
            if (_timing.InPrediction && _timing.IsFirstTimePredicted)
            {
                RaisePredictiveEvent(predictedEv);
            }
        }

        _transform.SetMapCoordinates(targetId, xeno.Comp.TargetCoordinates);

        // Fixes lunges done when hugging a wall that would otherwise not move you
        var coordinates = _transform.GetMapCoordinates(xeno);
        if (xeno.Comp.TargetCoordinates.MapId == coordinates.MapId &&
            !xeno.Comp.TargetCoordinates.InRange(coordinates, 1.25f))
        {
            var distance = xeno.Comp.TargetCoordinates.Position - coordinates.Position;
            var length = distance.Length();
            var newPosition = coordinates.Offset(((float) (length - 1.25) / length) * distance);
            _transform.SetMapCoordinates(xeno, newPosition);
        }

        xeno.Comp.AllowPullThroughBarbedBarricade = true;
        try
        {
            _pulling.TryStartPull(xeno, targetId);
        }
        finally
        {
            xeno.Comp.AllowPullThroughBarbedBarricade = false;
        }

        StopLunge(xeno);
        return true;

        var player = IoCManager.Resolve<ISharedPlayerManager>().Sessions.Select(e => e.Ping).Order();
    }
    private void HandleBarbedBarricadeHit(Entity<XenoActiveLungeComponent> xeno, EntityUid barricade)
    {
        if (xeno.Comp.BarbedBlocked || xeno.Comp.TargetDistance - xeno.Comp.NearestBarbedDistance < xeno.Comp.DistanceToBypassBarbedBarricade)
            return;

        xeno.Comp.BarbedBlocked = true;
        Dirty(xeno);

        var ev = new XenoLungeHitAttempt(xeno);
        RaiseLocalEvent(barricade, ref ev);
        StopLunge(xeno);
    }

    private void OnXenoLungeStunnedPullStopped(Entity<XenoLungeStunnedComponent> ent, ref PullStoppedMessage args)
    {
        if (args.PulledUid != ent.Owner)
            return;

        foreach (var effect in ent.Comp.Effects)
        {
            _statusEffects.TryRemoveStatusEffect(ent, effect);
        }

        RemCompDeferred<XenoLungeStunnedComponent>(ent.Owner);
    }

    private void OnXenoLungeHitAttempt(Entity<RMCLungeProtectionComponent> ent, ref XenoLungeHitAttempt args)
    {
        if (args.Cancelled)
            return;

        if (!TryComp(args.Lunging, out XenoActiveLungeComponent? lunging))
            return;

        if (HasComp<BarricadeComponent>(ent) &&
            (!TryComp(ent, out BarbedComponent? barbed) || !barbed.IsBarbed))
        {
            return;
        }

        _stun.TryParalyze(args.Lunging, ent.Comp.StunDuration, true);

        if (_net.IsServer)
            _audio.PlayPvs(ent.Comp.BlockSound, args.Lunging);

        var selfMessage = Loc.GetString("rmc-obstacle-slam-self", ("object", Identity.Name(ent, EntityManager, args.Lunging)));
        _popup.PopupClient(selfMessage, args.Lunging, args.Lunging, PopupType.MediumCaution);

        var others = Filter.PvsExcept(args.Lunging).Recipients;
        foreach (var other in others)
        {
            if (other.AttachedEntity is not { } otherEnt)
                continue;

            var othersMessage = Loc.GetString("rmc-obstacle-slam-others", ("ent", Identity.Name(args.Lunging, EntityManager, otherEnt)), ("object", Identity.Name(ent, EntityManager, otherEnt)));
            _popup.PopupEntity(othersMessage, args.Lunging, otherEnt, PopupType.MediumCaution);
        }

        args.Cancelled = true;
    }

    private void StopLunge(EntityUid lunging)
    {
        if (_physicsQuery.TryGetComponent(lunging, out var physics))
        {
            _physics.ResetDynamics(lunging, physics);
            _physics.SetLinearVelocity(lunging, Vector2.Zero, body: physics);
            _physics.SetAngularVelocity(lunging, 0f, body: physics);
            _physics.SetBodyStatus(lunging, physics, BodyStatus.OnGround);
        }

        RemCompDeferred<XenoActiveLungeComponent>(lunging);
    }

    public override void Update(float frameTime)
    {
        var time = _timing.CurTime;
        var stunnedQuery = EntityQueryEnumerator<XenoLungeStunnedComponent>();
        while (stunnedQuery.MoveNext(out var uid, out var stunned))
        {
            if (time < stunned.ExpireAt)
                continue;

            RemCompDeferred<XenoLungeStunnedComponent>(uid);
        }

        // var activeLungeQuery = EntityQueryEnumerator<XenoActiveLungeComponent>();
        // while (activeLungeQuery.MoveNext(out var uid, out var comp))
        // {
        //     if (!TryComp(uid, out ThrownItemComponent? thrown))
        //     {
        //         RemCompDeferred<XenoActiveLungeComponent>(uid);
        //         continue;
        //     }
        //
        //     if (comp.Origin.MapId != comp.TargetCoordinates.MapId)
        //     {
        //         _thrownItem.StopThrow(uid, thrown);
        //         continue;
        //     }
        //
        //     var coords = _transform.GetMapCoordinates(uid);
        //     var range = (comp.Origin.Position - comp.TargetCoordinates.Position).Length();
        //     if (!comp.Origin.InRange(coords, range))
        //     {
        //         if (!_pulling.IsPulling(uid))
        //             ApplyLungeHitEffects((uid, comp), comp.Target, true);
        //     }
        // }
    }
}

[ByRefEvent]
public record struct XenoLungeHitAttempt(EntityUid Lunging, bool Cancelled = false);
