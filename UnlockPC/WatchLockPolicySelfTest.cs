using System.Diagnostics;

namespace UnlockPC;

internal static class WatchLockPolicySelfTest
{
    public static int Run()
    {
        try
        {
            var policy = new WatchLockPolicy();

            policy.Reset(At(0));
            Check(!Observe(policy, false, 1, (1, -50)), "first strong packet while unlocked");
            Check(!Observe(policy, false, 24.62, (24.62, -50)), "23.62 seconds between strong packets");
            Check(!Observe(policy, false, 3600), "one hour of silence");

            policy.Reset(At(0));
            Check(!Observe(policy, false, 1.5, (1, -92), (1.25, -90), (1.5, -91)), "weak burst");
            Check(!Observe(policy, false, 15, (15, -90)), "weak packets across a long gap");
            Check(Observe(policy, false, 18, (18, -85)), "two very weak packets over three seconds");
            Check(!Observe(policy, false, 23.1), "stale weak evidence");

            policy.Reset(At(0));
            Check(!Observe(policy, false, 8, (1, -78), (4, -76), (7, -74), (8, -75)), "stronger signal clears weak run");
            Check(!Observe(policy, false, 11, (11, -75)), "two ordinary weak packets");
            Check(Observe(policy, false, 14, (14, -75)), "three ordinary weak packets over six seconds");
            Check(!Observe(policy, false, 14.5, (14.5, -50)), "strong recovery clears departure");
            Check(!WatchLockPolicy.InputAllowsLock(null) && !WatchLockPolicy.InputAllowsLock(TimeSpan.FromSeconds(14.99)) &&
                WatchLockPolicy.InputAllowsLock(TimeSpan.FromSeconds(15)), "input idle boundary");

            policy.Reset(At(0));
            Check(!Observe(policy, true, 1, (1, -45)), "manual lock with watch nearby");
            Check(!Observe(policy, true, 120, (120, -44)), "manual lock with silence and a strong packet");
            Check(!Observe(policy, true, 124, (121, -85), (124, -90)), "weak packets only arm return");
            Check(policy.ReturnReason == WatchReturnReason.ObservedDeparture, "armed after departure");
            Check(Observe(policy, true, 125, (125, -50)), "one strong packet after departure");

            policy.Reset(At(10), WatchReturnReason.AutomaticLock);
            Check(Observe(policy, true, 11, (11, -50)), "one packet after automatic lock");
            policy.Reset(At(20), WatchReturnReason.SystemResume);
            Check(!Observe(policy, true, 21, (19.9, -42), (21, -51), (22, -43)), "pre-resume, subthreshold and future packets");
            Check(!Observe(policy, true, 24, (21.5, -43)), "stale strong packet");
            Check(Observe(policy, true, 24, (24, -50)), "one new strong packet after resume");
            policy.Reset(At(25));
            Check(!Observe(policy, true, 26, (26, -45)), "later manual lock does not inherit resume");

            policy.Reset(At(30));
            Check(!Observe(policy, false, 37, (31, -85), (34, -88), (37, -50)), "strong recovery in the same batch");
            Check(!Observe(policy, false, 42, (42, -90), (42, -91), (41, -92)), "duplicate or out-of-order packets");
            policy.ResetSamples(At(43));
            Check(!Observe(policy, false, 46, (42.5, -90), (46, -90)), "pre-interruption samples are discarded");

            CheckReturnRules();
            CheckWeakSignalThreshold();
            CheckUnlockDelay();
            CheckResume();
            CheckInputActivity();
            CheckSignalDispatch();
            BluetoothLog.Write("presence_self_test_passed");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            BluetoothLog.Write("presence_self_test_failed", new { ex.Message });
            return 1;
        }
    }

    private static void CheckSignalDispatch()
    {
        var callbacks = new Queue<Action>();
        bool enabled = false;
        bool accepting = false;
        int reads = 0;
        var dispatch = new WatchSignalDispatch(() => enabled, callback =>
        {
            if (accepting)
                callbacks.Enqueue(callback);
            return accepting;
        }, () => reads++);

        dispatch.Signal();
        Check(callbacks.Count == 0, "unlocked broadcasts do not schedule immediate work");
        enabled = true;
        dispatch.Signal();
        Check(callbacks.Count == 0, "dispatcher rejection does not run the signal handler");
        accepting = true;
        dispatch.Signal();
        dispatch.Signal();
        Check(callbacks.Count == 1 && reads == 0, "locked broadcasts enqueue one immediate drain");
        callbacks.Dequeue()();
        Check(reads == 1, "signal handled by the dispatcher");
        dispatch.Signal();
        Check(callbacks.Count == 1, "a later broadcast can enqueue another drain");
    }

    private static void CheckInputActivity()
    {
        var gamepad = new GamepadState { LeftX = 0.04f, LeftY = 0.04f, RightTrigger = 0.01f };
        Check(!gamepad.IsActive, "neutral stick and trigger noise");
        gamepad.LeftX = 0.6f;
        for (int second = 0; second <= 12; second++)
            Check(gamepad.IsActive, "same stick position remains active past the lock idle window");
        gamepad.LeftX = 0;
        gamepad.RightTrigger = 0.4f;
        for (int second = 0; second <= 12; second++)
            Check(gamepad.IsActive, "same trigger position remains active past the lock idle window");
        gamepad.RightTrigger = 0;
        Check(!gamepad.IsActive, "released controls are idle");
        gamepad.Buttons = 1;
        Check(gamepad.IsActive, "held digital button");

        var reports = new HidReportActivity();
        Check(!reports.Observe(1, 3, 2, [1, 4, 0]), "truncated HID batch is rejected");
        Check(reports.Observe(1, 3, 2, [1, 4, 0, 2, 7, 0]), "first batch from a HID device");
        Check(!reports.Observe(1, 3, 2, [2, 7, 0, 1, 4, 0]), "unchanged interleaved HID reports are idle");
        Check(reports.Observe(1, 3, 1, [2, 8, 0]), "changed HID report is activity");
        Check(reports.Observe(2, 3, 1, [2, 8, 0]), "HID report history is per device");
        reports.Remove(1);
        Check(reports.Observe(1, 3, 1, [2, 8, 0]), "reconnected HID has fresh report history");
    }

    private static void CheckReturnRules()
    {
        var policy = new WatchLockPolicy();
        policy.SetRules(new WatchRuleSettings(ReturnRssi: -55, ReturnWindowSeconds: 4,
            ReturnPacketCount: 2, RequireWatchUnlocked: true), At(0));
        policy.Reset(At(0), WatchReturnReason.AutomaticLock);
        Check(policy.Observe(At(1), [new(At(1), -40)], locked: true) is null, "missing unlock flag is rejected");
        Check(policy.LastStrongSignalBlock == WatchUnlockBlock.WatchUnlockUnconfirmed, "missing flag cannot confirm watch unlock");
        Check(policy.Observe(At(2), [new(At(2), -40, 0x18)], locked: true) is null, "cleared unlock flag is rejected");
        Check(policy.Observe(At(3), [new(At(3), -40, 0xB8)], locked: true) is null, "conflicting flags are rejected");
        Check(policy.LastStrongSignalBlock == WatchUnlockBlock.WatchUnlockUnconfirmed, "conflicting flags are inconclusive");
        Check(policy.Observe(At(3.5), [new(At(3.5), -40, 0x28)], locked: true) is null &&
            policy.LastStrongSignalBlock == WatchUnlockBlock.WatchAppearsLocked, "locked watch has a distinct block reason");
        Check(policy.Observe(At(4), [new(At(4), -56, 0x98)], locked: true) is null, "custom threshold boundary");
        Check(policy.LastStrongSignalBlock is null, "a weak sample does not repeat the previous strong-signal block");
        Check(policy.Observe(At(5), [new(At(5), -55, 0x90)], locked: true) is null, "one qualified packet is insufficient");
        Check(policy.LastStrongSignalBlock == WatchUnlockBlock.WaitingForBroadcastCount, "insufficient qualified broadcasts have a distinct reason");
        Check(policy.Observe(At(9.1), [new(At(9.1), -55, 0x98)], locked: true) is null, "earlier packet expired");
        var decision = policy.Observe(At(10), [new(At(10), -54, 0x90)], locked: true);
        Check(decision is { SampleCount: 2, Rssi: -54 }, "two packets with unlock bit and custom limits");
    }

    private static void CheckWeakSignalThreshold()
    {
        var policy = new WatchLockPolicy();
        policy.SetRules(new WatchRuleSettings(WeakRssi: -80, VeryWeakRssi: -92), At(0));
        Check(!Observe(policy, false, 7, (1, -79), (4, -79), (7, -79)), "samples above the configured weak threshold");
        Check(Observe(policy, false, 14, (8, -80), (11, -80), (14, -80)), "configured weak threshold is inclusive");

        policy.Reset(At(0));
        Check(!Observe(policy, false, 4, (1, -91), (4, -91)), "two samples above the separate very weak threshold");
        policy.Reset(At(0));
        Check(Observe(policy, false, 4, (1, -92), (4, -92)), "separate very weak threshold is inclusive");
    }

    private static void CheckUnlockDelay()
    {
        var policy = new WatchLockPolicy();
        policy.SetRules(new WatchRuleSettings(UnlockDelaySeconds: 5), At(0));
        policy.Reset(At(10));
        Check(policy.Observe(At(14), [new(At(11), -88), new(At(14), -88)], locked: true, lockedAt: At(10)) is null &&
            policy.ReturnReason == WatchReturnReason.ObservedDeparture, "departure can be observed during unlock delay");
        Check(policy.Observe(At(15), [new(At(14.9), -44)], locked: true, lockedAt: At(10)) is null,
            "a buffered strong sample taken before the delay expires is rejected");
        Check(policy.LastStrongSignalBlock == WatchUnlockBlock.WaitingForDelay, "delay is reported ahead of watch state");
        Check(policy.Observe(At(15), [new(At(15), -44)], locked: true, lockedAt: At(10)) is not null,
            "new strong samples are eligible at the delay boundary");
    }

    private static void CheckResume()
    {
        var resume = new WatchResumeState();
        var policy = new WatchLockPolicy();
        Check(!resume.IsPending && !resume.IsRecoveringScan(At(1)), "startup has no resume eligibility");
        resume.Suspend();
        Check(resume.IsSuspended && !resume.IsPending, "suspended session does not process samples");
        Check(resume.Resume(At(10)) && !resume.IsSuspended, "system resume");
        Check(!resume.Resume(At(10.5)), "duplicate resume does not move the sample cutoff");
        Check(!resume.TryResolve(At(11), null, At(9), out _) && resume.IsPending, "unknown lock state keeps samples queued");
        Check(resume.TryResolve(At(12), true, At(9), out long? floor) && floor == At(10),
            "already locked before system resume");
        policy.Reset(floor.GetValueOrDefault(), WatchReturnReason.SystemResume);
        Check(!Observe(policy, true, 12, (10, -50)), "packets at or before resume are rejected");
        Check(Observe(policy, true, 12, (10.5, -50)), "first strong packet after system resume");
        Check(resume.TakeFirstRead() && !resume.TakeFirstRead(), "first read after resume is consumed once");

        resume.Suspend();
        Check(resume.Resume(At(20)), "new suspend-resume cycle");
        Check(resume.TryResolve(At(22), true, At(21), out floor) && floor == At(21),
            "lock notification after resume advances cutoff");
        policy.Reset(floor.GetValueOrDefault(), WatchReturnReason.SystemResume);
        Check(!Observe(policy, true, 22, (20.5, -50)), "pre-lock packet after resume is rejected");
        Check(Observe(policy, true, 22, (21.5, -50)), "first strong packet after lock notification");
        resume.ClearSession();
        Check(!resume.TakeFirstRead() && !resume.IsPending && !resume.Resume(At(23)),
            "unlock or switching console clears eligibility and rejects a duplicate resume");

        resume.Suspend();
        Check(resume.Resume(At(30)), "resume with an unlocked console");
        Check(resume.TryResolve(At(30.5), false, 0, out floor) && floor is null, "unlocked console does not arm unlock");
        Check(!resume.TryResolve(At(31), true, At(31), out floor), "later manual lock cannot reuse resume");

        resume.Suspend();
        Check(resume.Resume(At(40)), "resume with unavailable session state");
        Check(!resume.TryResolve(At(42.9), null, 0, out _), "wait within session-state deadline");
        Check(resume.TryResolve(At(43), null, 0, out floor) && floor is null && !resume.TakeFirstRead(),
            "unknown session state times out without unlock eligibility");
        Check(resume.CanRetryScan(At(43)) && !resume.CanRetryScan(At(43.5)), "space BLE retries one second apart");
        Check(resume.CanRetryScan(At(44)) && resume.CanRetryScan(At(45)) && resume.CanRetryScan(At(46)) &&
            !resume.CanRetryScan(At(47)), "at most four BLE restart attempts");
        Check(resume.IsRecoveringScan(At(50)) && !resume.IsRecoveringScan(At(50.1)), "BLE recovery deadline");
    }

    private static long At(double seconds) => (long)(seconds * Stopwatch.Frequency);

    private static bool Observe(WatchLockPolicy policy, bool locked, double now, params (double Seconds, short Rssi)[] packets) =>
        policy.Observe(At(now), packets.Select(packet => new WatchSignalSample(At(packet.Seconds), packet.Rssi)).ToArray(), locked) is not null;

    private static void Check(bool passed, string scenario)
    {
        if (!passed)
            throw new InvalidOperationException(scenario);
    }
}
