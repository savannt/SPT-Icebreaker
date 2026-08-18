using System;
using System.Reflection;
using SPTarkov.DI.Annotations;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Helpers.Bot;
using SPTarkov.Server.Core.Services.Bot;
using SPTarkov.Server.Core.Services.Items;
using SPTarkov.Server.Core.Services.Server;

namespace Manimal.Icebreaker.Server;

// PBS MASQUERADE, applied where it cannot be raced. the loot-firewall call sets the
// static during StartLocalRaid — but PBS's own router hook is a POST-processing chain
// (it receives the computed output and returns it), so it runs AFTER StartLocalRaid
// and stomps RaidInformation.RaidLocation back to 'Suburbs'. bot generation arrives on
// a LATER request and read the stomped value: every wave bot failed with
// "Map 'Suburbs' not found" while loot worked fine (measured, 2026-08-03).
//
// this DI override of BotGenerator re-applies the masquerade at the top of
// PrepareAndGenerateBot — per bot, inside the very call that reads the static, with
// nothing scheduled between. PBS overrides different generator classes
// (CustomBotWeaponGenerator / CustomBotEquipmentModGenerator), so no DI clash.
// 4.1.2 sealed the generator: BotGenerator.PrepareAndGenerateBot is no longer virtual and
// DatabaseService/ConfigServer are gone, so the DI-override this class used to be cannot
// exist any more. The hook is now a harmony prefix/postfix on that same method — which is
// strictly better than what it replaces: harmony cannot be displaced by another mod winning
// the registration race, so the "who owns the slot" failure mode this file was written
// around (APBS 2.2.x) is gone by construction. HoldsGeneratorSlot stays, still read by
// IcebreakerBotGenDiag to gate its fallback so specials never apply twice to one bot.
[Injectable(TypePriority = OnLoadOrder.PostLoad + 90000)]
public class IcebreakerBotFirewall(
    ISptLogger<IcebreakerBotFirewall> logger,
    RandomUtil randomUtil,
    TemplateTable templateTable) : IOnLoad
{
    private static ISptLogger<IcebreakerBotFirewall>? _log;
    private static RandomUtil? _rng;
    private static TemplateTable? _templates;
    private static bool _aliveLogged;

    // read by IcebreakerBotGenDiag so "we lost the generation path" reports itself instead
    // of being an ABSENT log line somebody has to know to grep for
    internal static bool HoldsGeneratorSlot;

    public Task OnLoadAsync(CancellationToken cancellationToken = default)
    {
        _log = logger;
        _rng = randomUtil;
        _templates = templateTable;
        try
        {
            var h = new Harmony("com.manimal.icebreaker.botfirewall");
            h.Patch(AccessTools.Method(typeof(BotGenerator), nameof(BotGenerator.PrepareAndGenerateBot)),
                prefix: new HarmonyMethod(typeof(IcebreakerBotFirewall), nameof(GeneratePrefix)),
                postfix: new HarmonyMethod(typeof(IcebreakerBotFirewall), nameof(GeneratePostfix)));
            HoldsGeneratorSlot = true;
            logger.Info("[Icebreaker] bot firewall armed (harmony hook on BotGenerator.PrepareAndGenerateBot)");
        }
        catch (Exception e)
        {
            HoldsGeneratorSlot = false;
            logger.Warning($"[Icebreaker] bot firewall could not patch BotGenerator ({e.Message}) — "
                + "the bot-generate tap will carry the masquerade and per-bot injections instead");
        }
        return Task.CompletedTask;
    }

    private static void GeneratePrefix()
    {
        if (_log == null) return;
        if (!_aliveLogged)
        {
            _aliveLogged = true;
            _log.Info("[Icebreaker] bot firewall generator ACTIVE (harmony hook holds the generation path)");
        }
        IcebreakerPbsMasquerade.Apply(_log);
    }

    private static void GeneratePostfix(BotBase __result, BotGenerationDetails botGenerationDetails)
    {
        if (_log == null || _rng == null || _templates == null) return;
        IcebreakerBotSpecials.Apply(__result, botGenerationDetails?.RoleLowercase, _rng, _templates, _log);
    }
}

internal static class IcebreakerPbsMasquerade
{
    private static bool _logged;
    private static PropertyInfo _prop;
    private static bool _resolved;

    internal static void Apply<T>(ISptLogger<T> log)
    {
        try
        {
            if (!_resolved)
            {
                _resolved = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var n = asm.GetName().Name ?? "";
                    if (n.IndexOf("progressivebotsystem", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("ProgressiveBotSystem", StringComparison.Ordinal) < 0) continue;

                    // fast path: the 2.0.0 layout we verified
                    var ri = asm.GetType("ProgressiveBotSystem.Models.RaidInformation");
                    _prop = ri?.GetProperty("RaidLocation", BindingFlags.Public | BindingFlags.Static);

                    // version-adaptive fallback (08-09 field log: APBS 2.2.0 on a player's
                    // server, no masquerade engagement, every vanilla bot dead with
                    // "Map 'Suburbs' not found" — if the type moved, find ANY static
                    // string RaidLocation in their assembly instead of giving up silently)
                    if (_prop == null)
                    {
                        Type[] types;
                        try { types = asm.GetTypes(); }
                        catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
                        foreach (var t in types)
                        {
                            if (t == null) continue;
                            var p = t.GetProperty("RaidLocation", BindingFlags.Public | BindingFlags.Static);
                            if (p != null && p.PropertyType == typeof(string) && p.CanWrite)
                            {
                                _prop = p;
                                log.Info($"[Icebreaker] APBS layout drifted — RaidLocation found on '{t.FullName}' (adaptive search)");
                                break;
                            }
                        }
                    }

                    if (_prop == null)
                        // presence without a hook point is the one state that must be LOUD:
                        // this exact silence cost a player every vanilla bot on the map
                        log.Warning($"[Icebreaker] Progressive Bot System assembly '{n}' is present but no static RaidLocation "
                            + "property was found — the labs masquerade CANNOT protect this raid; expect 'Map Suburbs not found' "
                            + "bot failures. report the APBS version so the shim can be updated");
                    break;
                }
            }
            if (_prop == null || _prop.PropertyType != typeof(string)) return; // PBS absent or drifted

            if (string.Equals(_prop.GetValue(null) as string, "Suburbs", StringComparison.OrdinalIgnoreCase))
            {
                _prop.SetValue(null, "laboratory"); // exact key casing from their map switch
                if (!_logged)
                {
                    _logged = true;
                    log.Info("[Icebreaker] Progressive Bot System detected — presenting the icebreaker as "
                        + "'laboratory' to it (tight-interior CQB, high-tier gear tables) so tiered bots "
                        + "generate instead of throwing on the unknown map");
                }
            }
        }
        catch (Exception e)
        {
            if (!_logged) { _logged = true; log.Warning($"[Icebreaker] PBS masquerade failed: {e.Message}"); }
        }
    }
}

// PER-BOT SPECIALS, callable from EITHER owner of the generation path (08-13). this
// used to be instance methods on IcebreakerBotFirewall, which meant it silently died
// whenever another mod won the BotGenerator DI slot — APBS 2.2.1 does exactly that, and
// jagrr's server ran a whole session with no BD dogtags and no wedge euro fix. so the
// work lives here, static, and BOTH paths call it:
//   * IcebreakerBotFirewall.PrepareAndGenerateBot, when we hold the slot
//   * IcebreakerBotGenDiag's response postfix, when we DON'T (harmony can't be evicted)
//
// EXACTLY ONE of those may run per bot. running SwapKeycardForDogtag twice would DELETE
// the tag it just created: on the second pass there is no keycard left, so the tag it
// made falls into the "spare card or duplicate tag" cull branch. the diag gates its call
// on IcebreakerBotFirewall.HoldsGeneratorSlot for that reason — do not remove that gate.
internal static class IcebreakerBotSpecials
{
    // WEDGE CARRIES EUROS (user call 08-08): the BlackDiv mod gives bossWedge dollar
    // stacks, but the icebreaker's economy runs on euros (heli fare, quests). tpl swap
    // at generation, stack counts untouched.
    private const string DollarsTpl = "5696686a4bdc2da3298b456a";
    private const string EurosTpl = "569668774bdc2da2298b4568";

    // the BlackDiv loadout hands every blackDivIb a shot at a Labs access keycard
    // (weight 60 in their pocket pool), an item with no business on this ship. so the
    // keycard IS the dogtag: swap it in place, ferrum common and green rare at 75/25
    // (user call 08-13). the keycard's own spawn rate becomes the tag drop rate.
    // SWAPPING a template needs no container logic, which is why this works at all —
    // every server-side ADD is dead on arrival, see the C-3 note in IcebreakerCrew.
    private const string BdDogtagGreenTpl = "6a461bf82b2264dbe10d0ee6";
    private const string BdDogtagFerrumTpl = "6a461aed7391ab085a093760";
    private const string BdDogtagRedTpl = "6a461c41ec88c6b9a509fb17";
    private const string LabsKeycardTpl = "5c94bbff86f7747ee735c08f";

    private static bool? _tagsPresent;
    private static bool _tagsWarned;

    // icebreaker raids only (raid-context latch); goons + rogues on vanilla maps stay
    // untouched by construction.
    internal static void Apply<T>(BotBase bot, string role, RandomUtil rng, TemplateTable db, ISptLogger<T> log)
    {
        try
        {
            if (!IcebreakerRaidContext.OnIcebreaker || bot?.Inventory == null) return;

            bool isWedge = string.Equals(role, "bosswedge", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(role, "blackdivib", StringComparison.OrdinalIgnoreCase) || isWedge)
                SwapKeycardForDogtag(bot, isWedge, rng, db, log);

            if (isWedge && bot.Inventory.Items != null)
            {
                int swapped = 0;
                foreach (var it in bot.Inventory.Items)
                    if (it != null && string.Equals(it.Template.ToString(), DollarsTpl, StringComparison.OrdinalIgnoreCase))
                    {
                        it.Template = new MongoId(EurosTpl);
                        swapped++;
                    }
                if (swapped > 0)
                    log.Info($"[Icebreaker] wedge currency fix — {swapped} dollar stack(s) swapped to euros");
            }
        }
        catch (Exception e)
        {
            log.Warning($"[Icebreaker] special-item injection failed: {e.Message}");
        }
    }

    // ONE TAG PER BODY (user call 08-12): the pocket pool can hand the same bot more
    // than one keycard, and a naive swap-them-all turns that into a pile of dogtags,
    // quietly devaluing the barter currency. so the FIRST keycard becomes the tag, every
    // other keycard is DELETED (getting labs cards off this ship was the whole point),
    // and any duplicate tag is culled too, whatever its source.
    private static void SwapKeycardForDogtag<T>(BotBase bot, bool isWedge, RandomUtil rng, TemplateTable db, ISptLogger<T> log)
    {
        var items = bot.Inventory?.Items;
        if (items is null || !TagsPresent(db, log)) return;

        var tagTpls = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { BdDogtagGreenTpl, BdDogtagFerrumTpl, BdDogtagRedTpl };

        Item first = null;
        var doomed = new List<Item>();
        foreach (var it in items)
        {
            if (it is null) continue;
            var tpl = it.Template.ToString();
            bool isCard = string.Equals(tpl, LabsKeycardTpl, StringComparison.OrdinalIgnoreCase);
            bool isTag = tagTpls.Contains(tpl);
            if (!isCard && !isTag) continue;
            if (first is null && isCard) first = it; // only a card can become the tag
            else doomed.Add(it);                     // spare cards + any duplicate tags
        }

        string what = null;
        if (first != null)
        {
            // 25 = green, the other 75 = ferrum
            bool green = rng.GetChance100(25.0);
            what = isWedge ? "red" : (green ? "green" : "ferrum");
            first.Template = new MongoId(isWedge ? BdDogtagRedTpl : (green ? BdDogtagGreenTpl : BdDogtagFerrumTpl));
        }
        foreach (var d in doomed) items.Remove(d);

        if (first != null || doomed.Count > 0)
            log.Info($"[Icebreaker] {(first != null ? $"labs keycard -> BD {what} dogtag" : "no keycard")} on "
                + $"{(isWedge ? "wedge" : "a black division body")}"
                + (doomed.Count > 0 ? $"; culled {doomed.Count} spare card/tag(s) — one tag per body" : ""));
    }

    // dependency guard for the BD dogtags — resolved once, warned once
    private static bool TagsPresent<T>(TemplateTable db, ISptLogger<T> log)
    {
        if (_tagsPresent == null)
        {
            // both, not just the one this roll wants — they ship in the same config, so
            // a half-present set means the dependency is broken either way
            try
            {
                var items = db.Items;
                _tagsPresent = items.ContainsKey(new MongoId(BdDogtagGreenTpl))
                            && items.ContainsKey(new MongoId(BdDogtagFerrumTpl));
            }
            catch { _tagsPresent = false; }
            if (_tagsPresent == false && !_tagsWarned)
            {
                _tagsWarned = true;
                log.Warning("[Icebreaker] BD dogtag templates not in the item DB (WTT-ContentBackport kordbreach set missing?) "
                    + "— black division bodies will carry no dogtags, and the barters will be unbuyable");
            }
        }
        return _tagsPresent == true;
    }
}
