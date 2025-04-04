using System;
using System.Collections.Generic;
using ACE.Common;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.WorldObjects;

public partial class Creature : Container
{
    public bool IsHumanoid
    {
        get => (this is Player || AiAllowedCombatStyle != CombatStyle.Undef);
    } // Our definition of humanoid in this case is a creature that can wield weapons.
    public bool IsExhausted
    {
        get => Stamina.Current == 0;
    }

    protected QuestManager _questManager;

    public QuestManager QuestManager
    {
        get
        {
            if (_questManager == null)
            {
                /*if (!(this is Player))
                    _log.Debug($"Initializing non-player QuestManager for {Name} (0x{Guid})", Name, Guid);*/

                _questManager = new QuestManager(this);
            }

            return _questManager;
        }
    }

    /// <summary>
    /// A table of players who currently have their targeting reticule on this creature
    /// </summary>
    private Dictionary<uint, WorldObjectInfo> selectedTargets;

    /// <summary>
    /// A list of ammo types and amount that we've been hit with. Used so we can drop some of that on our corpse.
    /// </summary>
    public Dictionary<uint, int> ammoHitWith;

    public Creature LastAttackedCreature;
    public double LastAttackTime;
    public Position LastHeartbeatPosition;

    /// <summary>
    /// A decaying count of attacks this creature has received recently.
    /// </summary>
    public int numRecentAttacksReceived;
    public float attacksReceivedPerSecond;

    /// <summary>
    /// A stored reference of a "Refused" item, to allow the TakeItem emote to take the specific guid shown to NPC
    /// </summary>
    public (WorldObject, uint?) RefusalItem;

    /// <summary>
    /// Currently used to handle some edge cases for faction mobs
    /// DamageHistory.HasDamager() has the following issues:
    /// - if a player attacks a same-factioned mob but is evaded, the mob would quickly de-aggro
    /// - if a player attacks a same-factioned mob in a group of same-factioned mobs, the other nearby faction mobs should be alerted, and should maintain aggro, even without a DamageHistory entry
    /// - if a summoner attacks a same-factioned mob, should the summoned CombatPet possibly defend the player in that situation?
    /// </summary>
    //public HashSet<uint> RetaliateTargets { get; set; }

    /// <summary>
    /// A new biota be created taking all of its values from weenie.
    /// </summary>
    public Creature(Weenie weenie, ObjectGuid guid)
        : base(weenie, guid)
    {
        InitializePropertyDictionaries();
        SetEphemeralValues();
    }

    /// <summary>
    /// Restore a WorldObject from the database.
    /// </summary>
    public Creature(Biota biota)
        : base(biota)
    {
        InitializePropertyDictionaries();
        SetEphemeralValues();
    }

    private void InitializePropertyDictionaries()
    {
        if (Biota.PropertiesAttribute == null)
        {
            Biota.PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>();
        }

        if (Biota.PropertiesAttribute2nd == null)
        {
            Biota.PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>();
        }

        if (Biota.PropertiesBodyPart == null)
        {
            Biota.PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>();
        }

        if (Biota.PropertiesSkill == null)
        {
            Biota.PropertiesSkill = new Dictionary<Skill, PropertiesSkill>();
        }
    }

    private void SetEphemeralValues()
    {
        CombatMode = CombatMode.NonCombat;
        DamageHistory = new DamageHistory(this);

        if (!(this is Player))
        {
            GenerateNewFace();
        }

        // If any of the vitals don't exist for this biota, one will be created automatically in the CreatureVital ctor
        Vitals[PropertyAttribute2nd.MaxHealth] = new CreatureVital(this, PropertyAttribute2nd.MaxHealth);
        Vitals[PropertyAttribute2nd.MaxStamina] = new CreatureVital(this, PropertyAttribute2nd.MaxStamina);
        Vitals[PropertyAttribute2nd.MaxMana] = new CreatureVital(this, PropertyAttribute2nd.MaxMana);

        // If any of the attributes don't exist for this biota, one will be created automatically in the CreatureAttribute ctor
        Attributes[PropertyAttribute.Strength] = new CreatureAttribute(this, PropertyAttribute.Strength);
        Attributes[PropertyAttribute.Endurance] = new CreatureAttribute(this, PropertyAttribute.Endurance);
        Attributes[PropertyAttribute.Coordination] = new CreatureAttribute(this, PropertyAttribute.Coordination);
        Attributes[PropertyAttribute.Quickness] = new CreatureAttribute(this, PropertyAttribute.Quickness);
        Attributes[PropertyAttribute.Focus] = new CreatureAttribute(this, PropertyAttribute.Focus);
        Attributes[PropertyAttribute.Self] = new CreatureAttribute(this, PropertyAttribute.Self);

        foreach (var kvp in Biota.PropertiesSkill)
        {
            Skills[kvp.Key] = new CreatureSkill(this, kvp.Key, kvp.Value);
        }

        if (Health.Current <= 0)
        {
            Health.Current = Health.MaxValue;
        }

        if (Stamina.Current <= 0)
        {
            Stamina.Current = Stamina.MaxValue;
        }

        if (Mana.Current <= 0)
        {
            Mana.Current = Mana.MaxValue;
        }

        if (!(this is Player))
        {
            GenerateWieldList();

            EquipInventoryItems();

            GenerateWieldedTreasure();

            EquipInventoryItems();

            GenerateInventoryTreasure();

            SetMonsterState();

            if (IsMonster)
            {
                Tier = SetTierByCreatureLevel();
                var qualityMod = LootQualityMod;
                if (qualityMod != null)
                {
                    DeathTreasure.LootQualityMod = (float)qualityMod;
                }

                ThreatLevel = new Dictionary<Creature, int>();
                PositiveThreat = new Dictionary<Creature, float>();
                NegativeThreat = new Dictionary<Creature, float>();
            }

            if (!Tier.HasValue && DeathTreasureType.HasValue)
            {
                var treasure = DeathTreasure;
                if (treasure != null)
                {
                    Tier = DeathTreasure.Tier;
                }
            }

            // Archetype System
            var useArchetypeSystem = UseArchetypeSystem ?? false;
            if (useArchetypeSystem)
            {
                var statWeight = 0.0f;
                var level = (float)(Level ?? 1);
                var tier = (Tier ?? 1) - 1;

                switch (tier)
                {
                    case 0:
                        statWeight = level / 9;
                        break;
                    case 1:
                        statWeight = (level - 10) / 10;
                        break;
                    case 2:
                        statWeight = (level - 20) / 10;
                        break;
                    case 3:
                        statWeight = (level - 30) / 10;
                        break;
                    case 4:
                        statWeight = (level - 40) / 10;
                        break;
                    case 5:
                        statWeight = (level - 50) / 25;
                        break;
                    case 6:
                        statWeight = (level - 75) / 25;
                        break;
                    case 7:
                        statWeight = (level - 100) / 25;
                        break;
                }

                var toughness = ArchetypeToughness ?? 1.0;
                var physicality = ArchetypePhysicality ?? 1.0;
                var dexterity = ArchetypeDexterity ?? 1.0;
                var magic = ArchetypeMagic ?? 1.0;
                var intelligence = ArchetypeIntelligence ?? 1.0;
                var lethality = ArchetypeLethality ?? 1.0;

                SetSkills(tier, statWeight, toughness, physicality, dexterity, magic, intelligence);

                SetVitals(tier, statWeight, toughness, physicality, dexterity, magic);

                SetDamageArmorWard(tier, statWeight, toughness, physicality, magic, lethality);

                var difficultyMod =
                    (toughness * 3 + physicality + dexterity + magic + intelligence + lethality * 3) / 10.0;

                //Console.WriteLine($"\nDIFFICULTY MOD: {difficultyMod}\n" +
                //    $"Tough: {toughness}, Phys: {physicality}, Dex: {dexterity}, Magic: {magic}, Int: {intelligence}, Lethal: {lethality}");

                SetXp(difficultyMod);
            }

            // TODO: fix tod data
            Health.Current = Health.MaxValue;
            Stamina.Current = Stamina.MaxValue;
            Mana.Current = Mana.MaxValue;
        }

        SetMonsterState();

        CurrentMotionState = new Motion(MotionStance.NonCombat, MotionCommand.Ready);

        selectedTargets = new Dictionary<uint, WorldObjectInfo>();
    }

    // verify logic
    public bool IsNPC => !(this is Player) && !Attackable && TargetingTactic == TargetingTactic.None;

    public void GenerateNewFace()
    {
        var cg = DatManager.PortalDat.CharGen;

        if (!Heritage.HasValue)
        {
            if (
                !string.IsNullOrEmpty(HeritageGroupName)
                && Enum.TryParse(HeritageGroupName.Replace("'", ""), true, out HeritageGroup heritage)
            )
            {
                Heritage = (int)heritage;
            }
        }

        if (!Gender.HasValue)
        {
            if (!string.IsNullOrEmpty(Sex) && Enum.TryParse(Sex, true, out Gender gender))
            {
                Gender = (int)gender;
            }
        }

        if (!Heritage.HasValue || !Gender.HasValue)
        {
#if DEBUG
            //if (!(NpcLooksLikeObject ?? false))
            //log.DebugFormat("Creature.GenerateNewFace: {0} (0x{1}) - wcid {2} - Heritage: {3} | HeritageGroupName: {4} | Gender: {5} | Sex: {6} - Data missing or unparsable, Cannot randomize face.", Name, Guid, WeenieClassId, Heritage, HeritageGroupName, Gender, Sex);
#endif
            return;
        }

        if (
            !cg.HeritageGroups.TryGetValue((uint)Heritage, out var heritageGroup)
            || !heritageGroup.Genders.TryGetValue((int)Gender, out var sex)
        )
        {
#if DEBUG
            _log.Debug(
                "Creature.GenerateNewFace: {Name} (0x{Guid}) - wcid {WeenieClassId} - Heritage: {Heritage} | HeritageGroupName: {HeritageGroupName} | Gender: {Gender} | Sex: {Sex} - Data invalid, Cannot randomize face.",
                Name,
                Guid,
                WeenieClassId,
                Heritage,
                HeritageGroupName,
                Gender,
                Sex
            );
#endif
            return;
        }

        PaletteBaseId = sex.BasePalette;

        var appearance = new Appearance
        {
            HairStyle = 1,
            HairColor = 1,
            HairHue = 1,

            EyeColor = 1,
            Eyes = 1,

            Mouth = 1,
            Nose = 1,

            SkinHue = 1
        };

        // Get the hair first, because we need to know if you're bald, and that's the name of that tune!
        if (sex.HairStyleList.Count > 1)
        {
            if (PropertyManager.GetBool("npc_hairstyle_fullrange").Item)
            {
                appearance.HairStyle = (uint)ThreadSafeRandom.Next(0, sex.HairStyleList.Count - 1);
            }
            else
            {
                appearance.HairStyle = (uint)ThreadSafeRandom.Next(0, Math.Min(sex.HairStyleList.Count - 1, 8)); // retail range data compiled by OptimShi
            }
        }
        else
        {
            appearance.HairStyle = 0;
        }

        if (sex.HairStyleList.Count < appearance.HairStyle)
        {
            _log.Warning(
                $"Creature.GenerateNewFace: {Name} (0x{Guid}) - wcid {WeenieClassId} - HairStyle = {appearance.HairStyle} | HairStyleList.Count = {sex.HairStyleList.Count} - Data invalid, Cannot randomize face."
            );
            return;
        }

        var hairstyle = sex.HairStyleList[Convert.ToInt32(appearance.HairStyle)];

        appearance.HairColor = (uint)ThreadSafeRandom.Next(0, sex.HairColorList.Count - 1);
        appearance.HairHue = ThreadSafeRandom.Next(0.0f, 1.0f);

        appearance.EyeColor = (uint)ThreadSafeRandom.Next(0, sex.EyeColorList.Count - 1);
        appearance.Eyes = (uint)ThreadSafeRandom.Next(0, sex.EyeStripList.Count - 1);

        appearance.Mouth = (uint)ThreadSafeRandom.Next(0, sex.MouthStripList.Count - 1);

        appearance.Nose = (uint)ThreadSafeRandom.Next(0, sex.NoseStripList.Count - 1);

        appearance.SkinHue = ThreadSafeRandom.Next(0.0f, 1.0f);

        //// Certain races (Undead, Tumeroks, Others?) have multiple body styles available. This is controlled via the "hair style".
        ////if (hairstyle.AlternateSetup > 0)
        ////    character.SetupTableId = hairstyle.AlternateSetup;

        if (!EyesTextureDID.HasValue)
        {
            EyesTextureDID = sex.GetEyeTexture(appearance.Eyes, hairstyle.Bald);
        }

        if (!DefaultEyesTextureDID.HasValue)
        {
            DefaultEyesTextureDID = sex.GetDefaultEyeTexture(appearance.Eyes, hairstyle.Bald);
        }

        if (!NoseTextureDID.HasValue)
        {
            NoseTextureDID = sex.GetNoseTexture(appearance.Nose);
        }

        if (!DefaultNoseTextureDID.HasValue)
        {
            DefaultNoseTextureDID = sex.GetDefaultNoseTexture(appearance.Nose);
        }

        if (!MouthTextureDID.HasValue)
        {
            MouthTextureDID = sex.GetMouthTexture(appearance.Mouth);
        }

        if (!DefaultMouthTextureDID.HasValue)
        {
            DefaultMouthTextureDID = sex.GetDefaultMouthTexture(appearance.Mouth);
        }

        if (!HeadObjectDID.HasValue)
        {
            HeadObjectDID = sex.GetHeadObject(appearance.HairStyle);
        }

        // Skin is stored as PaletteSet (list of Palettes), so we need to read in the set to get the specific palette
        var skinPalSet = DatManager.PortalDat.ReadFromDat<PaletteSet>(sex.SkinPalSet);
        if (!SkinPaletteDID.HasValue)
        {
            SkinPaletteDID = skinPalSet.GetPaletteID(appearance.SkinHue);
        }

        // Hair is stored as PaletteSet (list of Palettes), so we need to read in the set to get the specific palette
        var hairPalSet = DatManager.PortalDat.ReadFromDat<PaletteSet>(
            sex.HairColorList[Convert.ToInt32(appearance.HairColor)]
        );
        if (!HairPaletteDID.HasValue)
        {
            HairPaletteDID = hairPalSet.GetPaletteID(appearance.HairHue);
        }

        // Eye Color
        if (!EyesPaletteDID.HasValue)
        {
            EyesPaletteDID = sex.EyeColorList[Convert.ToInt32(appearance.EyeColor)];
        }
    }

    public virtual float GetBurdenMod()
    {
        return 1.0f; // override for players
    }

    /// <summary>
    /// This will be false when creature is dead and waits for respawn
    /// </summary>
    public bool IsAlive
    {
        get => Health.Current > 0;
    }

    /// <summary>
    /// Sends the network commands to move a player towards an object
    /// </summary>
    public void MoveToObject(WorldObject target, float? useRadius = null)
    {
        var distanceToObject = useRadius ?? target.UseRadius ?? 0.6f;

        var moveToObject = new Motion(this, target, MovementType.MoveToObject);
        moveToObject.MoveToParameters.DistanceToObject = distanceToObject;

        // move directly to portal origin
        //if (target is Portal)
        //moveToObject.MoveToParameters.MovementParameters &= ~MovementParams.UseSpheres;

        SetWalkRunThreshold(moveToObject, target.Location);

        EnqueueBroadcastMotion(moveToObject);
    }

    /// <summary>
    /// Sends the network commands to move a player towards a position
    /// </summary>
    public void MoveToPosition(ACE.Entity.Position position)
    {
        var moveToPosition = new Motion(this, position);
        moveToPosition.MoveToParameters.DistanceToObject = 0.0f;

        SetWalkRunThreshold(moveToPosition, position);

        EnqueueBroadcastMotion(moveToPosition);
    }

    public void SetWalkRunThreshold(Motion motion, ACE.Entity.Position targetLocation)
    {
        // FIXME: WalkRunThreshold (default 15 distance) seems to not be used automatically by client
        // player will always walk instead of run, and if MovementParams.CanCharge is sent, they will always charge
        // to remedy this, we manually calculate a threshold based on WalkRunThreshold

        var dist = Location.DistanceTo(targetLocation);
        if (dist >= motion.MoveToParameters.WalkRunThreshold / 2.0f) // default 15 distance seems too far, especially with weird in-combat walking animation?
        {
            motion.MoveToParameters.MovementParameters |= MovementParams.CanCharge;

            // TODO: find the correct runrate here
            // the default runrate / charge seems much too fast...
            //motion.RunRate = GetRunRate() / 4.0f;
            motion.RunRate = GetRunRate();
        }
    }

    /// <summary>
    /// This is raised by Player.HandleActionUseItem.<para />
    /// The item does not exist in the players possession.<para />
    /// If the item was outside of range, the player will have been commanded to move using DoMoveTo before ActOnUse is called.<para />
    /// When this is called, it should be assumed that the player is within range.
    ///
    /// This is the OnUse method.   This is just an initial implemention.   I have put in the turn to action at this point.
    /// If we are out of use radius, move to the object.   Once in range, let's turn the creature toward us and get started.
    /// Note - we may need to make an NPC class vs monster as using a monster does not make them turn towrad you as I recall. Og II
    ///  Also, once we are reading in the emotes table by weenie - this will automatically customize the behavior for creatures.
    /// </summary>
    public override void ActOnUse(WorldObject worldObject)
    {
        // handled in base.OnActivate -> EmoteManager.OnUse()
    }

    public override void OnCollideObject(WorldObject target)
    {
        if (target.ReportCollisions == false)
        {
            return;
        }

        if (target is Door door)
        {
            door.OnCollideObject(this);
        }
        else if (target is Hotspot hotspot)
        {
            hotspot.OnCollideObject(this);
        }
    }

    /// <summary>
    /// Called when a player selects a target
    /// </summary>
    public bool OnTargetSelected(Player player)
    {
        return selectedTargets.TryAdd(player.Guid.Full, new WorldObjectInfo(player));
    }

    /// <summary>
    /// Called when a player deselects a target
    /// </summary>
    public bool OnTargetDeselected(Player player)
    {
        return selectedTargets.Remove(player.Guid.Full);
    }

    /// <summary>
    /// Called when a creature's health changes
    /// </summary>
    public void OnHealthUpdate()
    {
        foreach (var kvp in selectedTargets)
        {
            var player = kvp.Value.TryGetWorldObject() as Player;

            if (player?.Session != null)
            {
                QueryHealth(player.Session);
            }
            else
            {
                selectedTargets.Remove(kvp.Key);
            }
        }
    }

    public int SetTierByCreatureLevel()
    {
        switch (Level)
        {
            case < 10:
                return 1;
            case < 20:
                return 2;
            case < 30:
                return 3;
            case < 40:
                return 4;
            case < 50:
                return 5;
            case < 75:
                return 6;
            case < 100:
                return 7;
            case <= 126:
                return 8;
        }
        return 1;
    }

    public List<Creature> GetNearbyMonsters(double distance)
    {
        var visibleTargets = new List<Creature>();

        foreach (var obj in PhysicsObj.ObjMaint.GetVisibleObjects(PhysicsObj.CurCell))
        {
            var creature = obj.WeenieObj.WorldObject as Creature;

            if (creature is null
                || !creature.IsMonster
                || GetDistance(creature) > distance
                || creature.WeenieClassId is 1020001
                || creature.Visibility)
            {
                continue;
            }

            visibleTargets.Add(creature);
        }

        var nearestTargets = BuildTargetDistance(visibleTargets);

        var sortedTargets = new List<Creature>();

        foreach (var target in nearestTargets)
        {
            sortedTargets.Add(target.Target);
        }

        return sortedTargets;
    }

    public int GetCreatureAvgTierHealth()
    {

        var statWeight = 0.0f;
        var level = (float)(Level ?? 1);
        var tier = (Tier ?? 1) - 1;

        switch (tier)
        {
            case 0:
                statWeight = level / 9;
                break;
            case 1:
                statWeight = (level - 10) / 10;
                break;
            case 2:
                statWeight = (level - 20) / 10;
                break;
            case 3:
                statWeight = (level - 30) / 10;
                break;
            case 4:
                statWeight = (level - 40) / 10;
                break;
            case 5:
                statWeight = (level - 50) / 25;
                break;
            case 6:
                statWeight = (level - 75) / 25;
                break;
            case 7:
                statWeight = (level - 100) / 25;
                break;
        }

        var avgHealth = enemyHealth[tier] + (enemyHealth[tier + 1] - enemyHealth[tier]) * statWeight;

        return (int)avgHealth;
    }
}
