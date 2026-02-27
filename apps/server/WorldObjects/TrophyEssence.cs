using System;
using System.Collections.Generic;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using Serilog;

namespace ACE.Server.WorldObjects;

public static class TrophyEssence
{
    private static readonly ILogger _log = Log.ForContext(typeof(TrophyEssence));

    private static readonly HashSet<uint> ValidCookTargets =
    [
        1053937, 1053938, 1053939,
        1053941, 1053942, 1053943,
        1053945, 1053946, 1053947,
        1053949, 1053950, 1053951
    ];

    private static readonly HashSet<uint> ValidAlchTargets =
    [
        1053957, 1053958, 1053959,
        1053961, 1053962, 1053963,
        1053965, 1053966, 1053967,
        1053969, 1053970, 1053971
    ];

    private static readonly int[] DifficultyByQuality =
    [
    //  Q1  Q2  Q3  Q4  Q5   Q6   Q7   Q8   Q9  Q10
        0, 20, 40, 60, 80, 100, 130, 160, 190, 220
    ];

    /// <summary>
    /// Maps base spell ID (quality 1) to icon overlay DID for the target consumable.
    /// </summary>
    private static readonly Dictionary<uint, uint> SpellIconOverlay = new()
    {
        // Cooking - Attributes
        { (uint)SpellId.CookFoodStrength1,      0x06009030 }, // Strength
        { (uint)SpellId.CookFoodEndurance1,     0x06009031 }, // Endurance
        { (uint)SpellId.CookFoodCoordination1,  0x06009032 }, // Coordination
        { (uint)SpellId.CookFoodQuickness1,     0x06009033 }, // Quickness
        { (uint)SpellId.CookFoodFocus1,         0x06009034 }, // Focus
        { (uint)SpellId.CookFoodSelf1,          0x06009035 }, // Self

        // Cooking - Skills
        { (uint)SpellId.CookFoodWarMagic1,      0x06009036 }, // War Magic
        { (uint)SpellId.CookFoodLifeMagic1,     0x06009037 }, // Life Magic
        { (uint)SpellId.CookFoodRun1,           0x06009038 }, // Run
        { (uint)SpellId.CookFoodJump1,          0x06009039 }, // Jump
        { (uint)SpellId.CookFoodThievery1,      0x06009040 }, // Thievery

        // Alchemy - Armor/Ward/Over Time
        { (uint)SpellId.AlchPotionArmorProtection1,       0x06009041 }, // Armor
        { (uint)SpellId.AlchPotionWardProtection1,        0x06009042 }, // Ward
        { (uint)SpellId.AlchPotionHealOverTime1,          0x06009043 }, // Heal Over Time
        { (uint)SpellId.AlchPotionStaminaOverTime1,       0x06009044 }, // Stamina Over Time
        { (uint)SpellId.AlchPotionManaOverTime1,          0x06009045 }, // Mana Over Time

        // Alchemy - Combat
        { (uint)SpellId.AlchPotionBloodDrinker1,          0x06009046 }, // Blood Drinker
        { (uint)SpellId.AlchPotionSpiritDrinker1,         0x06009047 }, // Spirit Drinker
        { (uint)SpellId.AlchPotionHeartSeeker1,           0x06009048 }, // Heart Seeker
        { (uint)SpellId.AlchPotionSwiftKiller1,           0x06009049 }, // Swift Killer
        { (uint)SpellId.AlchPotionDefender1,              0x06009050 }, // Defender
        { (uint)SpellId.AlchPotionCriticalChance1,        0x06009051 }, // Crit Chance
        { (uint)SpellId.AlchPotionCriticalDamage1,        0x06009052 }, // Crit Damage

        // Alchemy - Protections
        { (uint)SpellId.AlchPotionSlashingProtection1,    0x0600667C }, // Slashing
        { (uint)SpellId.AlchPotionPiercingProtection1,    0x06006681 }, // Piercing
        { (uint)SpellId.AlchPotionBludgeoningProtection1, 0x0600667D }, // Bludgeoning
        { (uint)SpellId.AlchPotionAcidProtection1,        0x0600667B }, // Acid
        { (uint)SpellId.AlchPotionFireProtection1,        0x0600667E }, // Fire
        { (uint)SpellId.AlchPotionColdProtection1,        0x0600667F }, // Cold
        { (uint)SpellId.AlchPotionLightningProtection1,   0x06006680 }, // Lightning

        // Alchemy - Over Time
    };

    /// <summary>
    /// Maps target WCID to the vital it restores for Chug essences.
    /// </summary>
    private static readonly Dictionary<uint, PropertyAttribute2nd> ChugBoosterEnum = new()
    {
        // Cooking targets
        { 1053937, PropertyAttribute2nd.Health },
        { 1053938, PropertyAttribute2nd.Stamina },
        { 1053939, PropertyAttribute2nd.Mana },
        { 1053941, PropertyAttribute2nd.Health },
        { 1053942, PropertyAttribute2nd.Stamina },
        { 1053943, PropertyAttribute2nd.Mana },
        { 1053945, PropertyAttribute2nd.Health },
        { 1053946, PropertyAttribute2nd.Stamina },
        { 1053947, PropertyAttribute2nd.Mana },
        { 1053949, PropertyAttribute2nd.Health },
        { 1053950, PropertyAttribute2nd.Stamina },
        { 1053951, PropertyAttribute2nd.Mana },

        // Alchemy targets
        { 1053957, PropertyAttribute2nd.Health },
        { 1053958, PropertyAttribute2nd.Stamina },
        { 1053959, PropertyAttribute2nd.Mana },
        { 1053961, PropertyAttribute2nd.Health },
        { 1053962, PropertyAttribute2nd.Stamina },
        { 1053963, PropertyAttribute2nd.Mana },
        { 1053965, PropertyAttribute2nd.Health },
        { 1053966, PropertyAttribute2nd.Stamina },
        { 1053967, PropertyAttribute2nd.Mana },
        { 1053969, PropertyAttribute2nd.Health },
        { 1053970, PropertyAttribute2nd.Stamina },
        { 1053971, PropertyAttribute2nd.Mana },
    };

    /// <summary>
    /// Returns the boost value for a Chug essence based on the vital type and quality.
    /// Health: quality * 50, Stamina/Mana: quality * 75.
    /// </summary>
    private static int GetChugBoostValue(PropertyAttribute2nd vital, int trophyQuality) => vital switch
    {
        PropertyAttribute2nd.Health => trophyQuality * 50,
        _ => trophyQuality * 75,
    };

    private const int CookingShortSharedCooldown = 10076;
    private const int AlchemyShortSharedCooldown = 10077;
    private const double ShortCooldownDuration = 300;

    // EssenceEffect values matching TrophySolvent.EssenceEffect enum
    private const int EffectLong = 1;
    private const int EffectShort = 2;

    /// <summary>
    /// Maps target WCID to its consumable tier (1-4).
    /// </summary>
    private static readonly Dictionary<uint, int> TargetTier = new()
    {
        // Cooking targets
        { 1053937, 1 }, { 1053938, 1 }, { 1053939, 1 },
        { 1053941, 2 }, { 1053942, 2 }, { 1053943, 2 },
        { 1053945, 3 }, { 1053946, 3 }, { 1053947, 3 },
        { 1053949, 4 }, { 1053950, 4 }, { 1053951, 4 },

        // Alchemy targets
        { 1053957, 1 }, { 1053958, 1 }, { 1053959, 1 },
        { 1053961, 2 }, { 1053962, 2 }, { 1053963, 2 },
        { 1053965, 3 }, { 1053966, 3 }, { 1053967, 3 },
        { 1053969, 4 }, { 1053970, 4 }, { 1053971, 4 },
    };

    /// <summary>
    /// Returns the minimum consumable tier required for a given essence quality.
    /// Q1-2 = any tier, Q3-4 = tier 2+, Q5-6 = tier 3+, Q7+ = tier 4 only.
    /// </summary>
    private static int GetMinimumTier(int trophyQuality) => trophyQuality switch
    {
        <= 2 => 1,
        <= 4 => 2,
        <= 6 => 3,
        _    => 4,
    };

    private static int GetDifficulty(int trophyQuality)
    {
        var index = Math.Clamp(trophyQuality, 1, DifficultyByQuality.Length) - 1;
        return DifficultyByQuality[index];
    }

    public static void HandleTrophyEssenceCrafting(Player player, WorldObject source, WorldObject target)
    {
        var isCookTarget = ValidCookTargets.Contains(target.WeenieClassId);
        var isAlchTarget = ValidAlchTargets.Contains(target.WeenieClassId);

        if (!isCookTarget && !isAlchTarget)
        {
            player.Session.Network.EnqueueSend(
                new GameMessageSystemChat(
                    $"The {target.NameWithMaterial} is not a valid target for this essence.",
                    ChatMessageType.Craft
                )
            );
            return;
        }

        // Check if the target already has an added spell from a TrophyEssence
        if (target.SpellDID != null && target.SpellDID != 0)
        {
            player.Session.Network.EnqueueSend(
                new GameMessageSystemChat(
                    $"The {target.NameWithMaterial} has already been enhanced with an essence.",
                    ChatMessageType.Craft
                )
            );
            return;
        }

        // Validate essence quality vs target tier
        var trophyQuality = source.TrophyQuality ?? 1;
        var minimumTier = GetMinimumTier(trophyQuality);
        var targetTier = TargetTier.GetValueOrDefault(target.WeenieClassId, 1);

        if (targetTier < minimumTier)
        {
            player.Session.Network.EnqueueSend(
                new GameMessageSystemChat(
                    $"The {source.NameWithMaterial} is too powerful for the {target.NameWithMaterial}. It requires a stronger consumable.",
                    ChatMessageType.Craft
                )
            );
            return;
        }

        var effectType = source.TrophyEssenceEffectType ?? 0;
        var isLong = effectType == EffectLong;
        var isShort = effectType == EffectShort;

        // Get the spell ID from the essence (may be null for booster-only essences)
        var spellId = source.TrophyEssenceSpellId;

        // Validate the essence's skill matches the target type
        var essenceSkill = (Skill)(source.TrophyEssenceSkill ?? 0);
        if ((isCookTarget && essenceSkill != Skill.Cooking) || (isAlchTarget && essenceSkill != Skill.Alchemy))
        {
            player.Session.Network.EnqueueSend(
                new GameMessageSystemChat(
                    $"The {source.NameWithMaterial} cannot be applied to the {target.NameWithMaterial}.",
                    ChatMessageType.Craft
                )
            );
            return;
        }

        if (!isLong && !isShort)
        {
            player.Session.Network.EnqueueSend(
                new GameMessageSystemChat(
                    $"The {source.NameWithMaterial} cannot be applied to the {target.NameWithMaterial}.",
                    ChatMessageType.Craft
                )
            );
            return;
        }

        // Skill check
        var difficulty = GetDifficulty(trophyQuality);
        var craftSkill = isCookTarget ? Skill.Cooking : Skill.Alchemy;
        var skill = player.GetCreatureSkill(craftSkill);
        var playerSkill = (int)skill.Current;
        var successChance = SkillCheck.GetSkillChance(playerSkill, difficulty);

        if (PropertyManager.GetBool("bypass_crafting_checks").Item)
        {
            successChance = 1.0;
        }

        var success = ThreadSafeRandom.Next(0.0f, 1.0f) < successChance;

        if (!success)
        {
            player.TryConsumeFromInventoryWithNetworking(source, 1);
            player.TryConsumeFromInventoryWithNetworking(target, 1);

            player.Session.Network.EnqueueSend(
                new GameMessageSystemChat(
                    $"You fail to apply the {source.NameWithMaterial} to the {target.NameWithMaterial}. Both items are destroyed.",
                    ChatMessageType.Craft
                )
            );

            _log.Debug(
                "[TROPHY_ESSENCE] {PlayerName} failed to apply {SourceName} to {TargetName} | Chance: {Chance}",
                player.Name,
                source.NameWithMaterial,
                target.NameWithMaterial,
                successChance
            );
            return;
        }

        // Apply spell and icon overlay if the essence has a spell
        var hasSpell = spellId != null && spellId != 0;

        if (hasSpell)
        {
            ApplySpellEffect(target, spellId.Value, trophyQuality);
        }

        // Apply effect-specific properties
        if (isLong)
        {
            // Long effects only apply spell (handled above)
        }
        else if (isShort)
        {
            ApplyShortEffect(target, trophyQuality, isCookTarget, hasSpell);
        }

        // Consume the essence
        player.TryConsumeFromInventoryWithNetworking(source, 1);

        // Update the target on the client so the new properties are visible
        player.EnqueueBroadcast(new GameMessageUpdateObject(target));

        player.Session.Network.EnqueueSend(
            new GameMessageSystemChat(
                $"You successfully apply the {source.NameWithMaterial} to the {target.NameWithMaterial}.",
                ChatMessageType.Craft
            )
        );

        _log.Debug(
            "[TROPHY_ESSENCE] {PlayerName} applied {SourceName} to {TargetName} | Effect: {Effect} | Chance: {Chance}",
            player.Name,
            source.NameWithMaterial,
            target.NameWithMaterial,
            isLong ? "Long" : "Short",
            successChance
        );
    }

    private static void ApplySpellEffect(WorldObject target, int spellId, int trophyQuality)
    {
        target.SetProperty(PropertyDataId.Spell, (uint)spellId);
        target.UiEffects = UiEffects.Magical;

        // Derive the base spell ID and apply the corresponding icon overlay
        var baseSpellId = (uint)(spellId - (trophyQuality - 1));
        if (SpellIconOverlay.TryGetValue(baseSpellId, out var iconOverlay))
        {
            target.IconOverlayId = iconOverlay;
        }
    }

    private static void ApplyShortEffect(WorldObject target, int trophyQuality, bool isCookTarget, bool hasSpell)
    {
        target.CooldownDuration = ShortCooldownDuration;
        target.CooldownId = isCookTarget ? CookingShortSharedCooldown : AlchemyShortSharedCooldown;

        // Only apply booster effects if the essence had no spell
        if (!hasSpell)
        {
            if (ChugBoosterEnum.TryGetValue(target.WeenieClassId, out var boosterEnum))
            {
                target.BoosterEnum = boosterEnum;
                target.BoostValue = GetChugBoostValue(boosterEnum, trophyQuality);
            }
        }

        target.SetProperty(PropertyString.Use, hasSpell
            ? "Use this item to gain the listed spell effect, for 30 seconds."
            : "Use this item to drink it.");

        // Remove the secondary spell
        target.Spell2 = null;
    }
}
