using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Structure;

namespace ACE.Server.WorldObjects;

partial class Player
{
    public bool SpellIsKnown(uint spellId)
    {
        return Biota.SpellIsKnown((int)spellId, BiotaDatabaseLock);
    }

    /// <summary>
    /// Will return true if the spell was added, or false if the spell already exists.
    /// </summary>
    public bool AddKnownSpell(uint spellId)
    {
        Biota.GetOrAddKnownSpell((int)spellId, BiotaDatabaseLock, out var spellAdded);

        if (spellAdded)
        {
            ChangesDetected = true;
        }

        return spellAdded;
    }

    /// <summary>
    /// Removes a known spell from the player's spellbook
    /// </summary>
    public bool RemoveKnownSpell(uint spellId)
    {
        return Biota.TryRemoveKnownSpell((int)spellId, BiotaDatabaseLock);
    }

    public void LearnSpellWithNetworking(uint spellId, bool uiOutput = true)
    {
        var spells = DatManager.PortalDat.SpellTable;

        if (!spells.Spells.ContainsKey(spellId))
        {
            var errorMessage = new GameMessageSystemChat("SpellID not found in Spell Table", ChatMessageType.Broadcast);
            Session.Network.EnqueueSend(errorMessage);

            _log.Error("SpellId: ({SpellId}) not found in Spell Table.", spellId);
            return;
        }

        if (!AddKnownSpell(spellId))
        {
            if (uiOutput)
            {
                var errorMessage = new GameMessageSystemChat("You already know that spell!", ChatMessageType.Broadcast);
                Session.Network.EnqueueSend(errorMessage);
            }
            return;
        }

        var updateSpellEvent = new GameEventMagicUpdateSpell(Session, (ushort)spellId);
        Session.Network.EnqueueSend(updateSpellEvent);

        // Check to see if we echo output to the client text area and do playscript animation
        if (uiOutput)
        {
            // Always seems to be this SkillUpPurple effect
            ApplyVisualEffects(PlayScript.SkillUpPurple);

            var message = $"You learn the {spells.Spells[spellId].Name} spell.\n";
            var learnMessage = new GameMessageSystemChat(message, ChatMessageType.Broadcast);
            Session.Network.EnqueueSend(learnMessage);
        }
        else
        {
            Session.Network.EnqueueSend(
                new GameEventCommunicationTransientString(Session, "You have learned a new spell.")
            );
        }
    }

    /// <summary>
    ///  Learns spells in bulk, without notification, filtered by school and level
    /// </summary>
    public void LearnSpellsInBulk(MagicSchool school, uint spellLevel, bool withNetworking = true)
    {
        var spellTable = DatManager.PortalDat.SpellTable;

        foreach (var spellID in PlayerSpellTable)
        {
            if (!spellTable.Spells.ContainsKey(spellID))
            {
                Console.WriteLine($"Unknown spell ID in PlayerSpellID table: {spellID}");
                continue;
            }
            var spell = new Spell(spellID, false);
            if (spell.School == school && spell.Formula.Level == spellLevel)
            {
                if (withNetworking)
                {
                    LearnSpellWithNetworking(spell.Id, false);
                }
                else
                {
                    AddKnownSpell(spell.Id);
                }
            }
        }
    }

    public void HandleActionMagicRemoveSpellId(uint spellId)
    {
        if (!Biota.TryRemoveKnownSpell((int)spellId, BiotaDatabaseLock))
        {
            _log.Error("Invalid spellId passed to Player.RemoveSpellFromSpellBook");
            return;
        }

        ChangesDetected = true;

        var removeSpellEvent = new GameEventMagicRemoveSpell(Session, (ushort)spellId);
        Session.Network.EnqueueSend(removeSpellEvent);
    }

    public void EquipItemFromSet(WorldObject item)
    {
        if (!item.HasItemSet)
        {
            return;
        }

        var setItems = EquippedObjects
            .Values.Where(i => i.HasItemSet && i.EquipmentSetId == item.EquipmentSetId)
            .ToList();

        var spells = GetSpellSet(setItems);

        // get the spells from before / without this item
        setItems.Remove(item);
        var prevSpells = GetSpellSet(setItems);

        EquipDequipItemFromSet(item, spells, prevSpells);
    }

    public void EquipDequipItemFromSet(
        WorldObject item,
        List<Spell> spells,
        List<Spell> prevSpells,
        WorldObject surrogateItem = null
    )
    {
        // compare these 2 spell sets -
        // see which spells are being added, and which are being removed
        var addSpells = spells.Except(prevSpells);
        var removeSpells = prevSpells.Except(spells);

        // set spells are not affected by mana
        // if it's equipped, it's active.

        foreach (var spell in removeSpells)
        {
            EnchantmentManager.Dispel(EnchantmentManager.GetEnchantment(spell.Id, item.EquipmentSetId.Value));
        }

        var addItem = surrogateItem ?? item;

        foreach (var spell in addSpells)
        {
            CreateItemSpell(addItem, spell.Id);
        }
    }

    public void DequipItemFromSet(WorldObject item)
    {
        if (!item.HasItemSet)
        {
            return;
        }

        var setItems = EquippedObjects
            .Values.Where(i => i.HasItemSet && i.EquipmentSetId == item.EquipmentSetId)
            .ToList();

        // for better bookkeeping, and to avoid a rarish error with AuditItemSpells detecting -1 duration item enchantments where
        // the CasterGuid is no longer in the player's possession
        var surrogateItem = setItems.LastOrDefault();

        var spells = GetSpellSet(setItems);

        // get the spells from before / with this item
        setItems.Add(item);
        var prevSpells = GetSpellSet(setItems);

        if (surrogateItem == null)
        {
            var addSpells = spells.Except(prevSpells);

            if (addSpells.Count() != 0)
            {
                _log.Error(
                    $"{Name}.DequipItemFromSet({item.Name}) -- last item in set dequipped, but addSpells still contains {string.Join(", ", addSpells.Select(i => i.Name))} -- this shouldn't happen!"
                );
            }
        }

        EquipDequipItemFromSet(item, spells, prevSpells, surrogateItem);
    }

    public void OnItemLevelUp(WorldObject item, int prevItemLevel)
    {
        if (!item.HasItemSet)
        {
            return;
        }

        var setItems = EquippedObjects
            .Values.Where(i => i.HasItemSet && i.EquipmentSetId == item.EquipmentSetId)
            .ToList();

        var levelDiff = prevItemLevel - (item.ItemLevel ?? 0);

        var prevSpells = GetSpellSet(setItems, levelDiff);

        var spells = GetSpellSet(setItems);

        EquipDequipItemFromSet(item, spells, prevSpells);
    }

    public void CreateSentinelBuffPlayers(IEnumerable<Player> players, bool self = false, ulong maxLevel = 8)
    {
        if (!(Session.AccessLevel >= AccessLevel.Sentinel))
        {
            return;
        }

        var SelfOrOther = self ? "Self" : "Other";

        // ensure level 8s are installed
        var maxSpellLevel = Math.Clamp(maxLevel, 1, 8);
        if (maxSpellLevel == 8 && DatabaseManager.World.GetCachedSpell((uint)SpellId.ArmorOther8) == null)
        {
            maxSpellLevel = 7;
        }

        var tySpell = typeof(SpellId);
        var buffMessages = new List<BuffMessage>();
        // prepare messages
        var buffsNotImplementedYet = new List<string>();
        foreach (var spell in Buffs)
        {
            var spellNamPrefix = spell;
            var isBane = false;
            if (spellNamPrefix.StartsWith("@"))
            {
                isBane = true;
                spellNamPrefix = spellNamPrefix.Substring(1);
            }
            var fullSpellEnumName = spellNamPrefix + ((isBane) ? string.Empty : SelfOrOther) + maxSpellLevel;
            var fullSpellEnumNameAlt =
                spellNamPrefix
                + ((isBane) ? string.Empty : ((SelfOrOther == "Self") ? "Other" : "Self"))
                + maxSpellLevel;
            var spellID = (uint)Enum.Parse(tySpell, fullSpellEnumName);
            var buffMsg = BuildBuffMessage(spellID);

            if (buffMsg == null)
            {
                spellID = (uint)Enum.Parse(tySpell, fullSpellEnumNameAlt);
                buffMsg = BuildBuffMessage(spellID);
            }

            if (buffMsg != null)
            {
                buffMsg.Bane = isBane;
                buffMessages.Add(buffMsg);
            }
            else
            {
                buffsNotImplementedYet.Add(fullSpellEnumName);
            }
        }
        // buff each player
        players
            .ToList()
            .ForEach(targetPlayer =>
            {
                if (buffMessages.Any(k => !k.Bane))
                {
                    // bake player into the messages
                    buffMessages.Where(k => !k.Bane).ToList().ForEach(k => k.SetTargetPlayer(targetPlayer));
                    // update client-side enchantments
                    targetPlayer.Session.Network.EnqueueSend(
                        buffMessages.Where(k => !k.Bane).Select(k => k.SessionMessage).ToArray()
                    );
                    // run client-side effect scripts, omitting duplicates
                    targetPlayer.EnqueueBroadcast(
                        buffMessages
                            .Where(k => !k.Bane)
                            .ToList()
                            .GroupBy(m => m.Spell.TargetEffect)
                            .Select(a => a.First().LandblockMessage)
                            .ToArray()
                    );
                    // update server-side enchantments

                    var buffsForPlayer = buffMessages.Where(k => !k.Bane).ToList().Select(k => k.Enchantment);

                    var lifeBuffsForPlayer = buffsForPlayer
                        .Where(k => k.Spell.School == MagicSchool.LifeMagic)
                        .ToList();
                    var critterBuffsForPlayer = buffsForPlayer
                        .Where(k => k.Spell.School == MagicSchool.CreatureEnchantment)
                        .ToList();
                    var itemBuffsForPlayer = buffsForPlayer
                        .Where(k => k.Spell.School == MagicSchool.PortalMagic)
                        .ToList();

                    lifeBuffsForPlayer.ForEach(spl =>
                    {
                        CreateEnchantmentSilent(spl.Spell, targetPlayer);
                    });
                    critterBuffsForPlayer.ForEach(spl =>
                    {
                        CreateEnchantmentSilent(spl.Spell, targetPlayer);
                    });
                    itemBuffsForPlayer.ForEach(spl =>
                    {
                        CreateEnchantmentSilent(spl.Spell, targetPlayer);
                    });
                }
                if (buffMessages.Any(k => k.Bane))
                {
                    // Impen/bane
                    var items = targetPlayer.EquippedObjects.Values.ToList();
                    var itembuffs = buffMessages.Where(k => k.Bane).ToList();
                    foreach (var itemBuff in itembuffs)
                    {
                        foreach (var item in items)
                        {
                            if ((item.WeenieType == WeenieType.Clothing || item.IsShield) && item.IsEnchantable)
                            {
                                CreateEnchantmentSilent(itemBuff.Spell, item);
                            }
                        }
                    }
                }
            });
    }

    private void CreateEnchantmentSilent(Spell spell, WorldObject target)
    {
        var addResult = target.EnchantmentManager.Add(spell, this, null);

        if (target is Player targetPlayer)
        {
            targetPlayer.Session.Network.EnqueueSend(
                new GameEventMagicUpdateEnchantment(
                    targetPlayer.Session,
                    new Enchantment(targetPlayer, addResult.Enchantment)
                )
            );

            targetPlayer.HandleSpellHooks(spell);
        }
    }

    // TODO: switch this over to SpellProgressionTables
    private static string[] Buffs = new string[]
    {
        #region spells
        // @ indicates impenetrability or a bane
        "Strength",
        "Invulnerability",
        "FireProtection",
        "Armor",
        "Rejuvenation",
        "Regeneration",
        "ManaRenewal",
        "Impregnability",
        "MagicResistance",
        //"AxeMastery",    // light weapons
        "LightWeaponsMastery",
        //"DaggerMastery", // finesse weapons
        "FinesseWeaponsMastery",
        //"MaceMastery",
        //"SpearMastery",
        //"StaffMastery",
        //"SwordMastery",  // heavy weapons
        "HeavyWeaponsMastery",
        //"UnarmedCombatMastery",
        //"BowMastery",    // missile weapons
        "MissileWeaponsMastery",
        //"CrossbowMastery",
        //"ThrownWeaponMastery",
        "AcidProtection",
        "CreatureEnchantmentMastery",
        "ItemEnchantmentMastery",
        "LifeMagicMastery",
        "WarMagicMastery",
        "ManaMastery",
        "ArcaneEnlightenment",
        "ArcanumSalvaging",
        "ArmorExpertise",
        "ItemExpertise",
        "MagicItemExpertise",
        "WeaponExpertise",
        "MonsterAttunement",
        "PersonAttunement",
        "DeceptionMastery",
        "HealingMastery",
        "LeadershipMastery",
        "LockpickMastery",
        "Fealty",
        "JumpingMastery",
        "Sprint",
        "BludgeonProtection",
        "ColdProtection",
        "LightningProtection",
        "BladeProtection",
        "PiercingProtection",
        "Endurance",
        "Coordination",
        "Quickness",
        "Focus",
        "Willpower",
        "CookingMastery",
        "FletchingMastery",
        "AlchemyMastery",
        "VoidMagicMastery",
        "SummoningMastery",
        "SwiftKiller",
        "Defender",
        "BloodDrinker",
        "HeartSeeker",
        "HermeticLink",
        "SpiritDrinker",
        "DualWieldMastery",
        "TwoHandedMastery",
        "DirtyFightingMastery",
        "RecklessnessMastery",
        "SneakAttackMastery",
        "ShieldMastery",
        "@Impenetrability",
        "@PiercingBane",
        "@BludgeonBane",
        "@BladeBane",
        "@AcidBane",
        "@FlameBane",
        "@FrostBane",
        "@LightningBane",
        #endregion
    };

    private class BuffMessage
    {
        public bool Bane { get; set; } = false;
        public GameEventMagicUpdateEnchantment SessionMessage { get; set; } = null;
        public GameMessageScript LandblockMessage { get; set; } = null;
        public Spell Spell { get; set; } = null;
        public Enchantment Enchantment { get; set; } = null;

        public void SetTargetPlayer(Player p)
        {
            Enchantment.Target = p;
            SessionMessage = new GameEventMagicUpdateEnchantment(p.Session, Enchantment);
            SetLandblockMessage(p.Guid);
        }

        public void SetLandblockMessage(ObjectGuid target)
        {
            LandblockMessage = new GameMessageScript(target, Spell.TargetEffect, 1f);
        }
    }

    private static BuffMessage BuildBuffMessage(uint spellID)
    {
        var buff = new BuffMessage();
        buff.Spell = new Spell(spellID);
        if (buff.Spell.NotFound)
        {
            return null;
        }

        buff.Enchantment = new Enchantment(
            null,
            0,
            spellID,
            1,
            (EnchantmentMask)buff.Spell.StatModType,
            buff.Spell.StatModVal
        );
        return buff;
    }

    public void HandleSpellbookFilters(SpellBookFilterOptions filters)
    {
        Character.SpellbookFilters = (uint)filters;
    }

    public void HandleSetDesiredComponentLevel(uint component_wcid, uint amount)
    {
        // ensure wcid is spell component
        if (!SpellComponent.IsValid(component_wcid))
        {
            _log.Warning(
                $"{Name}.HandleSetDesiredComponentLevel({component_wcid}, {amount}): invalid spell component wcid"
            );
            return;
        }
        if (amount > 0)
        {
            var existing = Character.GetFillComponent(component_wcid, CharacterDatabaseLock);

            if (existing == null)
            {
                Character.AddFillComponent(component_wcid, amount, CharacterDatabaseLock, out var exists);
            }
            else
            {
                existing.QuantityToRebuy = (int)amount;
            }
        }
        else
        {
            Character.TryRemoveFillComponent(component_wcid, out var _, CharacterDatabaseLock);
        }

        CharacterChangesDetected = true;
    }

    public static Dictionary<MagicSchool, uint> FociWCIDs = new Dictionary<MagicSchool, uint>()
    {
        { MagicSchool.CreatureEnchantment, 15268 }, // Foci of Enchantment
        { MagicSchool.PortalMagic, 15269 }, // Foci of Artifice
        { MagicSchool.LifeMagic, 15270 }, // Foci of Verdancy
        { MagicSchool.WarMagic, 15271 }, // Foci of Strife
        { MagicSchool.VoidMagic, 43173 }, // Foci of Shadow
    };

    public bool HasFoci(MagicSchool school)
    {
        switch (school)
        {
            case MagicSchool.CreatureEnchantment:
                if (AugmentationInfusedCreatureMagic > 0)
                {
                    return true;
                }

                break;
            case MagicSchool.PortalMagic:
                if (AugmentationInfusedItemMagic > 0)
                {
                    return true;
                }

                break;
            case MagicSchool.LifeMagic:
                if (AugmentationInfusedLifeMagic > 0)
                {
                    return true;
                }

                break;
            case MagicSchool.VoidMagic:
                if (AugmentationInfusedVoidMagic > 0)
                {
                    return true;
                }

                break;
            case MagicSchool.WarMagic:
                if (AugmentationInfusedWarMagic > 0)
                {
                    return true;
                }

                break;
        }

        var wcid = FociWCIDs[school];
        return Inventory.Values.FirstOrDefault(i => i.WeenieClassId == wcid) != null;
    }

    public void HandleSpellHooks(Spell spell)
    {
        HandleMaxVitalUpdate(spell);

        // unsure if spell hook was here in retail,
        // but this has the potential to take the client out of autorun mode
        // which causes them to stop if they hit a turn key afterwards
        if (PropertyManager.GetBool("runrate_add_hooks").Item)
        {
            HandleRunRateUpdate(spell);
        }
    }

    /// <summary>
    /// Called when an enchantment is added or removed,
    /// checks if the spell affects the max vitals,
    /// and if so, updates the client immediately
    /// </summary>
    public void HandleMaxVitalUpdate(Spell spell)
    {
        var maxVitals = spell.UpdatesMaxVitals;

        if (maxVitals.Count == 0)
        {
            return;
        }

        var actionChain = new ActionChain();
        actionChain.AddDelaySeconds(1.0f); // client needs time for primary attribute updates
        actionChain.AddAction(
            this,
            () =>
            {
                foreach (var maxVital in maxVitals)
                {
                    var playerVital = Vitals[maxVital];

                    Session.Network.EnqueueSend(
                        new GameMessagePrivateUpdateAttribute2ndLevel(this, playerVital.ToEnum(), playerVital.Current)
                    );
                }
            }
        );
        actionChain.EnqueueChain();
    }

    public bool HandleRunRateUpdate(Spell spell)
    {
        if (!spell.UpdatesRunRate)
        {
            return false;
        }

        return HandleRunRateUpdate();
    }

    public void AuditItemSpells(bool peaceMode = false)
    {
        // cleans up bugged chars with dangling item set spells
        // from previous bugs

        var allPossessions = GetAllPossessions().ToDictionary(i => i.Guid, i => i);

        // this is a legacy method, but is still a decent failsafe to catch any existing issues

        // get active item enchantments -- exclude vitae, level scaler, and mana reservation
        var enchantments = Biota
            .PropertiesEnchantmentRegistry.Clone(BiotaDatabaseLock)
            .Where(i => i.Duration == -1 && i.SpellId != (int)SpellId.Vitae
                                         && i.SpellId != (int)SpellId.CurseWeakness1
                                         && i.SpellId != (int)SpellId.HealthReservationPenalty
                                         && i.SpellId != (int)SpellId.StaminaReservationPenalty
                                         && i.SpellId != (int)SpellId.ManaReservationPenalty)
            .ToList();

        foreach (var enchantment in enchantments)
        {
            var table = enchantment.HasSpellSetId ? allPossessions : EquippedObjects;

            // if this item is not equipped, remove enchantment
            if (!table.TryGetValue(new ObjectGuid(enchantment.CasterObjectId), out var item))
            {
                var spell = new Spell(enchantment.SpellId, false);

                if (spell.Id is (uint)SpellId.CombatModeRunDebuff or (uint)SpellId.StealthRunDebuff)
                {
                    continue;
                }

                var trinket = GetEquippedTrinket();
                if (IsCombatFocusSpell(enchantment, trinket))
                {
                    continue;
                }

                if (
                    spell.Id != (uint)SpellId.Ardence
                    && spell.Id != (uint)SpellId.Vim
                    && spell.Id != (uint)SpellId.Volition
                    && spell.Id != (uint)SpellId.OntheRun
                )
                {
                    _log.Error(
                        $"{Name}.AuditItemSpells(): removing spell {spell.Name} from {(enchantment.HasSpellSetId ? "non-possessed" : "non-equipped")} item"
                    );
                }

                EnchantmentManager.Dispel(enchantment);
                continue;
            }

            // is this item part of a set?
            if (!item.HasItemSet)
            {
                continue;
            }

            // get all of the equipped items in this set
            var setItems = EquippedObjects
                .Values.Where(i => i.HasItemSet && i.EquipmentSetId == item.EquipmentSetId)
                .ToList();

            // get all of the spells currently active from this set
            var currentSpells = GetSpellSet(setItems);

            // get all of the spells possible for this item set
            var possibleSpells = GetSpellSetAll((EquipmentSet)item.EquipmentSetId);

            // get the difference between them
            var inactiveSpells = possibleSpells.Except(currentSpells).ToList();

            // remove any item set spells that shouldn't be active
            foreach (var inactiveSpell in inactiveSpells)
            {
                var removeSpells = enchantments
                    .Where(i => i.SpellSetId == item.EquipmentSetId && i.SpellId == inactiveSpell.Id)
                    .ToList();

                foreach (var removeSpell in removeSpells)
                {
                    if (inactiveSpell.Id == (uint)SpellId.MireFoot && !peaceMode)
                    {
                        continue;
                    }

                    if (
                        inactiveSpell.Id != (uint)SpellId.Ardence
                        && inactiveSpell.Id != (uint)SpellId.Vim
                        && inactiveSpell.Id != (uint)SpellId.Volition
                        && inactiveSpell.Id != (uint)SpellId.OntheRun
                        && inactiveSpell.Id != (uint)SpellId.MireFoot
                    )
                    {
                        _log.Error(
                            $"{Name}.AuditItemSpells(): removing spell {inactiveSpell.Name} from {item.EquipmentSetId}"
                        );
                    }

                    EnchantmentManager.Dispel(removeSpell);
                }
            }
        }
    }

    private bool IsCombatFocusSpell(PropertiesEnchantmentRegistry enchantment, WorldObject trinket)
    {
        if (trinket == null || trinket.WeenieType != WeenieType.CombatFocus)
        {
            return false;
        }

        var combatFocus = trinket as CombatFocus;

        var spellList = combatFocus.GetCurrentSpellList();

        foreach (var spellId in spellList)
        {
            if (new Spell(spellId).Category == enchantment.SpellCategory)
            {
                return true;
            }
        }

        return false;
    }

    public void GetAllSpellStacks()
    {
        GetOlthoiNorthSpellStacks(true);
    }

    public uint GetOlthoiNorthSpellStacks(bool withMessage = false)
    {
        uint totalStacks = 0;

        var msg = "Olthoi North Modifiers - ";

        var olthoiStaminaDebuff = EnchantmentManager.GetEnchantment((uint)SpellId.OlthoiStaminaDebuff); // stamaina and mana
        if (olthoiStaminaDebuff != null)
        {
            var staminaDebuff = olthoiStaminaDebuff.SpellStacks + 1;
            totalStacks += staminaDebuff;
            msg += $" Stamina/Mana: -{staminaDebuff}%";
        }

        var olthoiHealthDebuff = EnchantmentManager.GetEnchantment((uint)SpellId.OlthoiHealthDebuff);
        if (olthoiHealthDebuff != null)
        {
            var healthDebuff = olthoiHealthDebuff.SpellStacks + 1;
            totalStacks += healthDebuff;
            msg += $" Health: -{healthDebuff}%";
        }

        var olthoiDefenseDebuff = EnchantmentManager.GetEnchantment((uint)SpellId.OlthoiDefenseDebuff);
        if (olthoiDefenseDebuff != null)
        {
            var physicalDefenseDebuff = olthoiDefenseDebuff.SpellStacks + 1;
            totalStacks += physicalDefenseDebuff;
            msg += $" Physical Defense: -{physicalDefenseDebuff}%";
        }

        var olthoiAcidDebuff = EnchantmentManager.GetEnchantment((uint)SpellId.OlthoiAcidVulnerability);
        if (olthoiAcidDebuff != null)
        {
            var acidResistanceDebuff = olthoiAcidDebuff.SpellStacks + 1;
            totalStacks += acidResistanceDebuff;
            msg += $" Acid Vulnerability: +{acidResistanceDebuff}%.";
        }

        msg += $"\nTotal discovery bonus: +{totalStacks}% ";

        if (withMessage)
        {
            Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));
        }
        return totalStacks;
    }

    public bool IsShrouded()
    {
        var enchantments = Biota
            .PropertiesEnchantmentRegistry.Clone(BiotaDatabaseLock)
            .Where(i => i.Duration == -1 && i.SpellId != (int)SpellId.Vitae)
            .ToList();

        foreach (var enchantment in enchantments)
        {
            if (enchantment.SpellId == (int)SpellId.CurseWeakness1)
            {
                return true;
            }
        }

        return false;
    }
}
