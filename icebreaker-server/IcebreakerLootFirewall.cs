using System;
using System.Threading;
using System.Threading.Tasks;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.InRaid;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Generators.Loot;
using SPTarkov.Server.Core.Helpers.InRaid;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Services.Items;
using SPTarkov.Server.Core.Services.Locales;
using SPTarkov.Server.Core.Services.Server;

namespace Manimal.Icebreaker.Server;

// which map the ACTIVE raid runs on, latched by the loot firewall at StartLocalRaid.
// bot generation arrives on later requests with no location in its payload — this is
// how per-map bot tweaks (the knight's SZ-1 charge) know they're on the ship. single
// active raid per server holds for local SPT and fika alike.
internal static class IcebreakerRaidContext
{
    internal static bool OnIcebreaker;
}

// LOOT ISOLATION — on the icebreaker, loot generation runs with every third-party
// harmony patch on LocationLootGenerator SUSPENDED, then restored. mods keep working
// on every other map; on ours, authored loot generates exactly as intended.
//
// why not reverse-patch pristine copies (the first attempt): pristine is one level
// deep. LotsofLoot hooks PRIVATE CreateStaticLootItem, which the pristine copy of
// GenerateStaticContainers still CALLS — the call lands in the detour and their
// per-map dictionary throws from beneath us (fresh-install repro, 2026-08-03; the
// original fatal was the same mod's GenerateDynamicLoot prefix hanging
// /client/match/local/start with no response). suspend/restore has no depth limit:
// every method of the class is swept, so any hook by any mod at any level is out of
// the picture for exactly one call.
//
// mechanics: DI override (registrator walks base types; mod registrations land after
// core = last-wins, the CompoundingPerf pattern) makes this subclass THE generator.
// GenerateLocationLoot is the single entry the raid-start path uses; around its base
// call we Harmony.GetPatchInfo every declared method, Unpatch each foreign owner's
// patches, and re-Patch them afterward under their own owner ids with their own
// priorities/before/after so ownership and ordering survive.
//
// the try/catch stays as the last line: if something still throws (a mod hooking a
// DIFFERENT class entirely), the raid starts lootless with the culprit named — which
// still beats the fatal-with-no-response infinite loading screen.
// 4.1.2 sealed LocationLootGenerator.GenerateLocationLoot (no longer virtual) and removed
// DatabaseService/ConfigServer, so the DI override this class used to be is impossible. The
// same wrap is now built from two harmony seams:
//
//   * LocationLifecycleService.GenerateLocationAndLoot — the ONLY caller of the generator.
//     Suspending/restoring foreign patches happens here, one frame OUTSIDE the generator,
//     so we never unpatch a method we are currently executing inside.
//   * LocationLootGenerator.GenerateLocationLoot — the latch, knight restore, masquerade,
//     and the lootless-rather-than-hanging finalizer, exactly where they were before.
[Injectable(TypePriority = OnLoadOrder.PostLoad + 90000)]
public class IcebreakerLootFirewall(
    ISptLogger<IcebreakerLootFirewall> logger,
    LocationTable locationTable) : IOnLoad
{
    private const string OwnPrefix = "com.manimal.icebreaker";
    private static readonly object Gate = new();
    private static ISptLogger<IcebreakerLootFirewall>? _log;
    private static LocationTable? _locations;

    private sealed record Suspended(MethodBase Target, HarmonyLib.Patch Patch, HarmonyPatchType Kind);

    private static bool Ours(string id) => string.Equals(id, "suburbs", StringComparison.OrdinalIgnoreCase);

    public Task OnLoadAsync(CancellationToken cancellationToken = default)
    {
        _log = logger;
        _locations = locationTable;
        try
        {
            var h = new Harmony(OwnPrefix + ".lootfirewall");
            h.Patch(AccessTools.Method(typeof(LocationLifecycleService), nameof(LocationLifecycleService.GenerateLocationAndLoot)),
                prefix: new HarmonyMethod(typeof(IcebreakerLootFirewall), nameof(IsolatePrefix)),
                finalizer: new HarmonyMethod(typeof(IcebreakerLootFirewall), nameof(IsolateFinalizer)));
            h.Patch(AccessTools.Method(typeof(LocationLootGenerator), nameof(LocationLootGenerator.GenerateLocationLoot)),
                prefix: new HarmonyMethod(typeof(IcebreakerLootFirewall), nameof(GeneratePrefix)),
                finalizer: new HarmonyMethod(typeof(IcebreakerLootFirewall), nameof(GenerateFinalizer)));
            logger.Info("[Icebreaker] loot firewall armed (isolation on GenerateLocationAndLoot, latch on GenerateLocationLoot)");
        }
        catch (Exception e)
        {
            logger.Error($"[Icebreaker] loot firewall could NOT arm ({e.Message}) — third-party loot patches will "
                + "run against the icebreaker's authored loot, and the raid-context latch is dead. expect wrong loot");
        }
        return Task.CompletedTask;
    }

    // GOON-SYSTEM GUARD (2026-08-11, found while moving spawns onto BSG's wave generator).
    // SPT relocates the goons daily by zeroing BossChance on EVERY bossKnight row on EVERY
    // map, then restoring it on one randomly-chosen map from its own location pool — a pool
    // our map will never be in. our T1 knight row is a bossKnight row, so it gets zeroed and
    // the knight simply never arrives. the zeroing hits the LIVE database while the client
    // is served a CLONE taken moments earlier, so restoring here (after the clone, every
    // raid start, whatever map is loading) leaves the database correct for the next clone.
    // runs for other maps too — otherwise a customs raid between two icebreaker raids
    // leaves ours zeroed for the next one.
    private static void RestoreKnightChance()
    {
        try
        {
            var suburbs = _locations?.Suburbs?.Base?.BossLocationSpawn;
            if (suburbs is null) return;
            foreach (var row in suburbs)
                if (row.BossName == "bossKnight" && row.BossChance != 100)
                {
                    row.BossChance = 100;
                    _log?.Debug("[Icebreaker] knight T1 spawn chance restored to 100 (SPT's goon relocation had zeroed it)");
                }
        }
        catch (Exception e) { _log?.Warning($"[Icebreaker] knight chance restore failed: {e.Message}"); }
    }

    // ---- outer seam: suspend every third-party patch on the generator for this one call ----

    private static void IsolatePrefix(string name, ref List<Suspended>? __state)
    {
        __state = null;
        if (!Ours(name)) return;
        Monitor.Enter(Gate); // raid starts are rare; simplest way to keep suspend/restore atomic
        try
        {
            __state = SuspendForeign();
            if (__state.Count > 0)
                _log?.Info($"[Icebreaker] loot isolation: {__state.Count} third-party patch(es) on "
                    + "LocationLootGenerator suspended for this raid's generation "
                    + $"({string.Join(", ", OwnersOf(__state))}) — restored right after");
        }
        catch (Exception e)
        {
            _log?.Warning($"[Icebreaker] loot isolation could not suspend foreign patches: {e.Message}");
        }
    }

    private static void IsolateFinalizer(string name, List<Suspended>? __state)
    {
        if (!Ours(name)) return;
        try { if (__state != null) Restore(__state); }
        finally { Monitor.Exit(Gate); }
    }

    // ---- inner seam: latch, knight restore, masquerade, lootless-not-hanging ----

    private static void GeneratePrefix(string locationId)
    {
        // raid-context latch: loot generates at StartLocalRaid, bots on later requests —
        // this is the only place the server tells us which map the active raid is on
        IcebreakerRaidContext.OnIcebreaker = Ours(locationId);
        RestoreKnightChance();
        if (!Ours(locationId) || _log == null) return;

        // PBS MASQUERADE (policy exception, user-approved 2026-08-03): Progressive
        // Bot System keys hardcoded per-map switches off its static
        // RaidInformation.RaidLocation — 'Suburbs' throws for EVERY bot the server
        // generates (empty map). its router hook writes that static BEFORE
        // StartLocalRaid, we run INSIDE it, and bot generation reads it after — so
        // overwriting here makes PBS run its LABS profile on the icebreaker.
        IcebreakerPbsMasquerade.Apply(_log);
    }

    // the try/catch stays as the last line: if something still throws (a mod hooking a
    // DIFFERENT class entirely), the raid starts lootless with the culprit named — which
    // still beats the fatal-with-no-response infinite loading screen.
    private static Exception? GenerateFinalizer(string locationId, Exception? __exception,
        ref List<SpawnpointTemplate>? __result)
    {
        if (__exception == null || !Ours(locationId)) return __exception;
        _log?.Error("[Icebreaker] a mod threw inside loot generation for the icebreaker — "
            + "starting the raid WITHOUT generated loot rather than hanging the client. "
            + $"culprit: {Culprit(__exception)} — report it upstream. inner error: {__exception.Message}");
        __result = new List<SpawnpointTemplate>();
        return null; // swallowed
    }

    private static List<Suspended> SuspendForeign()
    {
        var outp = new List<Suspended>();
        var h = new Harmony(OwnPrefix + ".lootisolation");
        foreach (var m in typeof(LocationLootGenerator).GetMethods(
                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            var info = Harmony.GetPatchInfo(m);
            if (info == null) continue;
            Collect(outp, m, info.Prefixes, HarmonyPatchType.Prefix);
            Collect(outp, m, info.Postfixes, HarmonyPatchType.Postfix);
            Collect(outp, m, info.Transpilers, HarmonyPatchType.Transpiler);
            Collect(outp, m, info.Finalizers, HarmonyPatchType.Finalizer);
        }
        foreach (var s in outp)
            h.Unpatch(s.Target, s.Patch.PatchMethod);
        return outp;
    }

    private static void Collect(List<Suspended> outp, MethodBase m, IEnumerable<HarmonyLib.Patch> patches, HarmonyPatchType kind)
    {
        foreach (var p in patches ?? Array.Empty<HarmonyLib.Patch>())
            if (!p.owner.StartsWith(OwnPrefix, StringComparison.OrdinalIgnoreCase))
                outp.Add(new Suspended(m, p, kind));
    }

    private static void Restore(List<Suspended> suspended)
    {
        foreach (var s in suspended)
        {
            try
            {
                // re-applied under the ORIGINAL owner id with the original ordering
                // metadata, so a later unpatch by that mod still finds its own patch
                var h = new Harmony(s.Patch.owner);
                var hm = new HarmonyMethod(s.Patch.PatchMethod)
                {
                    priority = s.Patch.priority,
                    before = s.Patch.before?.Length > 0 ? s.Patch.before : null,
                    after = s.Patch.after?.Length > 0 ? s.Patch.after : null,
                };
                h.Patch(s.Target,
                    prefix: s.Kind == HarmonyPatchType.Prefix ? hm : null,
                    postfix: s.Kind == HarmonyPatchType.Postfix ? hm : null,
                    transpiler: s.Kind == HarmonyPatchType.Transpiler ? hm : null,
                    finalizer: s.Kind == HarmonyPatchType.Finalizer ? hm : null,
                    ilmanipulator: null);
            }
            catch
            {
                // a failed restore only means that mod's loot feature stays off for the
                // rest of THIS server session — still strictly better than the crash
            }
        }
    }

    private static IEnumerable<string> OwnersOf(List<Suspended> s)
    {
        var seen = new HashSet<string>();
        foreach (var x in s) if (seen.Add(x.Patch.owner)) yield return x.Patch.owner;
    }

    private static string Culprit(Exception e)
    {
        foreach (var line in (e.StackTrace ?? "").Split('\n'))
        {
            var m = Regex.Match(line, @"at (?!SPTarkov\.|System\.|Microsoft\.|DMD|SyncProxy)([A-Za-z_][\w]*)[\w.]*\.");
            if (m.Success) return m.Groups[1].Value;
        }
        return "unknown (see stack in this log)";
    }
}
