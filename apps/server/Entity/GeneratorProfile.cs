using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ACE.Common;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Physics.Common;
using ACE.Server.WorldObjects;
using Serilog;
using Quaternion = System.Numerics.Quaternion;

namespace ACE.Server.Entity;

/// <summary>
/// A generator profile for a Generator
/// </summary>
public class GeneratorProfile
{
    private readonly ILogger _log = Log.ForContext<GeneratorProfile>();

    /// <summary>
    /// The id for the profile. This id will be either a GUID from Landblock_Instances or an incremental id based on profile order from biota entry.
    /// </summary>
    public uint Id;

    public string LinkId => Id > 0x70000000 ? $"0x{Id:X8}" : $"{Id}";

    /// <summary>
    /// The biota with all the generator profile info
    /// </summary>
    public PropertiesGenerator Biota;

    /// <summary>
    /// A list of objects that have been spawned by this generator
    /// Mapping of object guid => registry node, which provides a bunch of detailed info about the spawn
    /// </summary>
    public readonly Dictionary<uint, WorldObjectInfo> Spawned = new Dictionary<uint, WorldObjectInfo>();

    /// <summary>
    /// The list of pending times awaiting respawning
    /// </summary>
    public readonly List<DateTime> SpawnQueue = new List<DateTime>();

    /// <summary>
    /// Returns TRUE if this profile is a placeholder object
    /// Placeholder objects are used for linkable generators,
    /// and are used as a template for the real items contained in the links.
    /// </summary>
    public bool IsPlaceholder
    {
        get => WeenieClassId == 3666;
    }

    /// <summary>
    /// TRUE if this Profile generated treasure using TreasureGenerator
    /// </summary>
    public bool GeneratedTreasureItem { get; private set; }

    /// <summary>
    /// The total # of active spawned objects + awaiting spawning
    /// </summary>
    public int CurrentCreate
    {
        get
        {
            if (!GeneratedTreasureItem)
            {
                return Spawned.Count + SpawnQueue.Count;
            }
            else
            {
                if (Spawned.Count > 0 || SpawnQueue.Count > 0)
                {
                    return 1;
                }
                else
                {
                    return 0;
                }
            }
        }
    }

    /// <summary>
    /// Returns the InitCreate for this generator profile
    /// </summary>
    public int InitCreate
    {
        get => Biota.InitCreate;
    }

    /// <summary>
    /// Returns the MaxCreate for this generator profile
    /// </summary>
    public int MaxCreate
    {
        get => Biota.MaxCreate;
    }

    /// <summary>
    /// Returns the WeenieClassId for this generator profile
    /// </summary>
    public uint WeenieClassId
    {
        get => Biota.WeenieClassId;
    }

    /// <summary>
    /// Flag indicates if generator profile is performing the initial spawn (TRUE / default),
    /// or the respawn (false)
    /// </summary>
    public bool FirstSpawn { get; set; } = true;

    /// <summary>
    /// The delay for respawning objects
    /// </summary>
    public float Delay
    {
        get
        {
            if (Generator is Chest && (Generator.ActivationResponse == ActivationResponse.Use || FirstSpawn))
            {
                return 0;
            }

            return Biota.Delay ?? Generator.GeneratorProfiles[0].Biota.Delay ?? 0.0f;
        }
    }

    /// <summary>
    /// DateTime for when the profile is available as a possible spawn choice
    /// </summary>
    public DateTime NextAvailable { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Returns TRUE if this profile is not currently on timed-out as a result of being notified of destruction/pick-up
    /// </summary>
    public bool IsAvailable => DateTime.UtcNow > NextAvailable;

    /// <summary>
    /// Returns TRUE if this profile MaxCreate is not infinite (-1) and CurrentCreate does not currently meet or exceed MaxCreate
    /// </summary>
    public bool IsMaxed => MaxCreate != -1 && CurrentCreate >= MaxCreate;

    /// <summary>
    /// The generator world object for this profile
    /// </summary>
    public WorldObject Generator;

    public RegenLocationType RegenLocationType => (RegenLocationType)Biota.WhereCreate;

    /// <summary>
    /// Constructs a new active generator profile
    /// from a biota generator
    /// </summary>
    public GeneratorProfile(WorldObject generator, PropertiesGenerator biota, uint profileId)
    {
        Generator = generator;
        Biota = biota;
        Id = profileId;
    }

    /// <summary>
    /// Called upon Generate request<para />
    /// Processes the SpawnQueue
    /// </summary>
    public void Spawn_HeartBeat(int? tier = null)
    {
        if (SpawnQueue.Count > 0)
        {
            ProcessQueue(tier);
        }
    }

    /// <summary>
    /// Determines the spawn times for initial object spawning,
    /// and for respawning
    /// </summary>
    public DateTime GetSpawnTime()
    {
        //if (Generator.CurrentlyPoweringUp || Generator.CachedRegenerationInterval >= Delay)
        return DateTime.UtcNow;
        //else
        //    return DateTime.UtcNow.AddSeconds(Delay);
    }

    /// <summary>
    /// Enqueues 1 or multiple objects from this generator profile
    /// adds these items to the spawn queue
    /// </summary>
    public void Enqueue(int numObjects = 1)
    {
        for (var i = 0; i < numObjects; i++)
        {
            /*if (MaxObjectsSpawned)
            {
                _log.Debug("{GeneratorName}.Enqueue({NumObjects}): max objects reached", _generator.Name, numObjects);
                break;
            }*/
            SpawnQueue.Add(GetSpawnTime());
        }
    }

    /// <summary>
    /// Spawns generator objects at the correct SpawnTime
    /// Called on when Generate is requested
    /// </summary>
    public void ProcessQueue(int? tier = null)
    {
        var index = 0;

        while (index < SpawnQueue.Count)
        {
            var queuedTime = SpawnQueue[index];

            if (queuedTime > DateTime.UtcNow)
            {
                // not time to spawn yet
                index++;
                continue;
            }

            if (MaxCreate == -1 || Spawned.Count < MaxCreate)
            {
                var objects = Spawn(tier);

                if (objects != null)
                {
                    foreach (var obj in objects)
                    {
                        var woi = new WorldObjectInfo(obj);

                        Spawned.Add(obj.Guid.Full, woi);
                    }
                }
            }
            else
            {
                // this shouldn't happen (hopefully)
                _log.Warning(
                    "[GENERATOR] 0x{GeneratorGuid}:{GeneratorWeenieClassId} ProcessQueue(): {LinkId}:{BiotaWeenieClassId} object(s) enqueued for {GeneratorName}, but MaxCreate({MaxCreate}) already reached!",
                    Generator.Guid,
                    Generator.WeenieClassId,
                    LinkId,
                    Biota.WeenieClassId,
                    Generator.Name,
                    MaxCreate
                );
            }

            SpawnQueue.RemoveAt(index);
        }

        FirstSpawn = false;
    }

    /// <summary>
    /// Spawns an object from the generator queue
    /// for RNG treasure, can spawn multiple objects
    /// If an object failed to spawn, but FirstSpawn is true, the object will still be returned as a spawned item, but, it will have been Destroy()'d first.
    /// </summary>
    public List<WorldObject> Spawn(int? tier = null)
    {
        var objects = new List<WorldObject>();

        if (RegenLocationType.HasFlag(RegenLocationType.Treasure))
        {
            objects = TreasureGenerator(tier);

            if (objects != null && objects.Count > 0)
            {
                Generator.GeneratedTreasureItem = true;
                GeneratedTreasureItem = true;
            }

            // Consolidate stackable items
            var stacksToRemove = new List<WorldObject>();
            if (objects != null)
            {
                foreach (var objectOne in objects.Where(objectOne => objectOne.MaxStackSize > 1 && !stacksToRemove.Contains(objectOne)))
                {
                    foreach (var objectTwo in objects)
                    {
                        if (objectOne.Guid == objectTwo.Guid || objectOne.WeenieClassId != objectTwo.WeenieClassId || stacksToRemove.Contains(objectTwo))
                        {
                            continue;
                        }

                        var newStack = objectOne.StackSize + objectTwo.StackSize;
                        objectOne.SetStackSize(newStack);

                        stacksToRemove.Add(objectTwo);
                        break;
                    }
                }
            }

            foreach (var item in stacksToRemove)
            {
                if (objects != null && objects.Contains(item))
                {
                    objects.Remove(item);
                }
            }
        }
        else
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(Biota.WeenieClassId);
            if (wo == null)
            {
                _log.Warning(
                    $"[GENERATOR] 0x{Generator.Guid}:{Generator.WeenieClassId} {Generator.Name}.Spawn(): failed to create wcid {Biota.WeenieClassId}"
                );
                return null;
            }

            if (wo is Creature { Attackable: true, UseArchetypeSystem: not true } creature)
            {
                // _log.Warning(
                //     "[GENERATOR] Preventing spawn of '{CreatureName}' ({CreatureWcid}) due to not using archetype system. Landblock: {Landblock}, Loc: {Loc}",
                //     creature.Name, creature.WeenieClassId, Generator.CurrentLandblock.Id, Generator.Location.ToLOCString());

                return null;
            }

            if (Biota.PaletteId.HasValue && Biota.PaletteId > 0)
            {
                wo.PaletteTemplate = (int)Biota.PaletteId;
            }

            if (Biota.Shade.HasValue && Biota.Shade > 0)
            {
                wo.Shade = Biota.Shade;
            }

            if ((Biota.Shade.HasValue && Biota.Shade > 0) || (Biota.PaletteId.HasValue && Biota.PaletteId > 0))
            {
                wo.CalculateObjDesc(); // to update icon
            }

            if (Biota.StackSize.HasValue && Biota.StackSize > 0)
            {
                wo.SetStackSize(Biota.StackSize);
            }

            objects.Add(wo);
        }

        var spawned = new List<WorldObject>();

        if (objects != null)
        {
            foreach (var obj in objects)
            {
                //log.DebugFormat("{0}.Spawn({1})", _generator.Name, obj.Name);

                obj.Generator = Generator;
                obj.GeneratorId = Generator.Guid.Full;

                var success = false;

                if (RegenLocationType.HasFlag(RegenLocationType.Specific))
                {
                    success = Spawn_Specific(obj);
                }
                else if (RegenLocationType.HasFlag(RegenLocationType.Scatter))
                {
                    success = Spawn_Scatter(obj);
                }
                else if (RegenLocationType.HasFlag(RegenLocationType.Contain))
                {
                    success = Spawn_Container(obj);
                }
                else if (RegenLocationType.HasFlag(RegenLocationType.Shop))
                {
                    success = Spawn_Shop(obj);
                }
                else
                {
                    success = Spawn_Default(obj);
                }

                // if first spawn fails, don't continually attempt to retry
                if (success || FirstSpawn)
                {
                    spawned.Add(obj);
                }

                // If the object failed to spawn, we still destroy it. This cleans up the object and releases the GUID.
                // This object still may be returned in the spawned collection if FirstSpawn is true. This is to prevent retry spam.
                if (!success)
                {
                    _log.Debug(
                        "[GENERATOR] 0x{GeneratorGuid}:{GeneratorWeenieClassId} {GeneratorName}.Spawn(): failed to spawn {WorldObjectName} (0x{WorldObjectGuid}:{WorldObjectWeenieClassId}) from profile {LinkId} at {RegenLocationType}\nGenerator Location: {GeneratorLocation}\nWorld Object Location: {WorldObjectLocation}",
                        Generator.Guid,
                        Generator.WeenieClassId,
                        Generator.Name,
                        obj.Name,
                        obj.Guid,
                        obj.WeenieClassId,
                        LinkId,
                        RegenLocationType,
                        Generator.Location?.ToLOCString(),
                        obj.Location.ToLOCString()
                    );
                    obj.Destroy();
                }
            }
        }

        return spawned;
    }

    /// <summary>
    /// Spawns an object at a specific position
    /// </summary>
    public bool Spawn_Specific(WorldObject obj)
    {
        // specific position
        if ((Biota.ObjCellId ?? 0) > 0)
        {
            var loc = Biota.ObjCellId;
            // allow generator slots to use whichever lb the generator is on, while keeping the cellId
            if (Biota.ObjCellId != Generator.Location.LandblockId.Raw)
            {
                var originalCellId = ((int)Biota.ObjCellId).ToString("X8");
                var generatorLandblockId = ((int)Generator.Location.LandblockId.Raw).ToString("X8");

                var modifiedCellId = generatorLandblockId.Substring(0, 4) + originalCellId.Substring(4);

                loc = uint.Parse(modifiedCellId, System.Globalization.NumberStyles.HexNumber);
            }
            obj.Location = new ACE.Entity.Position(
                loc ?? 0,
                Biota.OriginX ?? 0,
                Biota.OriginY ?? 0,
                Biota.OriginZ ?? 0,
                Biota.AnglesX ?? 0,
                Biota.AnglesY ?? 0,
                Biota.AnglesZ ?? 0,
                Biota.AnglesW ?? 0
            );
        }
        // offset from generator location
        else
        {
            if (PropertyManager.GetBool("use_generator_rotation_offset").Item)
            {
                var offset = Vector3.Transform(
                    new Vector3(Biota.OriginX ?? 0, Biota.OriginY ?? 0, Biota.OriginZ ?? 0),
                    Generator.Location.Rotation
                );

                if (Generator.GeneratorType == GeneratorType.Relative)
                {
                    var rotate =
                        new Quaternion(Biota.AnglesX ?? 0, Biota.AnglesY ?? 0, Biota.AnglesZ ?? 0, Biota.AnglesW ?? 0)
                        * Generator.Location.Rotation;

                    obj.Location = new ACE.Entity.Position(
                        Generator.Location.Cell,
                        Generator.Location.PositionX + offset.X,
                        Generator.Location.PositionY + offset.Y,
                        Generator.Location.PositionZ + offset.Z,
                        rotate.X,
                        rotate.Y,
                        rotate.Z,
                        rotate.W
                    );
                }
                else
                {
                    obj.Location = new ACE.Entity.Position(
                        Generator.Location.Cell,
                        Generator.Location.PositionX + offset.X,
                        Generator.Location.PositionY + offset.Y,
                        Generator.Location.PositionZ + offset.Z,
                        Biota.AnglesX ?? 0,
                        Biota.AnglesY ?? 0,
                        Biota.AnglesZ ?? 0,
                        Biota.AnglesW ?? 0
                    );
                }
            }
            else
            {
                obj.Location = new ACE.Entity.Position(
                    Generator.Location.Cell,
                    Generator.Location.PositionX + Biota.OriginX ?? 0,
                    Generator.Location.PositionY + Biota.OriginY ?? 0,
                    Generator.Location.PositionZ + Biota.OriginZ ?? 0,
                    Biota.AnglesX ?? 0,
                    Biota.AnglesY ?? 0,
                    Biota.AnglesZ ?? 0,
                    Biota.AnglesW ?? 0
                );
            }
        }

        obj.Location.PositionZ += 0.05f;

        if (!VerifyLandblock(obj) || !VerifyWalkableSlope(obj))
        {
            return false;
        }

        return obj.EnterWorld();
    }

    public bool Spawn_Scatter(WorldObject obj)
    {
        var genRadius = (float)(Generator.GetProperty(PropertyFloat.GeneratorRadius) ?? 0f);
        obj.Location = new ACE.Entity.Position(Generator.Location);

        // Skipping using same offset code above for offsetting scatter pos due to issues with rotation that were not expected at time content was rebuilt (Colo, others)
        // perhaps it should be same or similar but not able to spend time on verifying it out and making rotational adjustments at this time.

        //if (PropertyManager.GetBool("use_generator_rotation_offset").Item)
        //{
        //    var offset = Vector3.Transform(new Vector3(Biota.OriginX ?? 0, Biota.OriginY ?? 0, Biota.OriginZ ?? 0), Generator.Location.Rotation);

        //    obj.Location = new ACE.Entity.Position(Generator.Location.Cell, Generator.Location.PositionX + offset.X, Generator.Location.PositionY + offset.Y, Generator.Location.PositionZ + offset.Z, Biota.AnglesX ?? 0, Biota.AnglesY ?? 0, Biota.AnglesZ ?? 0, Biota.AnglesW ?? 0);
        //}
        //else
        //    obj.Location = new ACE.Entity.Position(Generator.Location.Cell, Generator.Location.PositionX + Biota.OriginX ?? 0, Generator.Location.PositionY + Biota.OriginY ?? 0, Generator.Location.PositionZ + Biota.OriginZ ?? 0, Biota.AnglesX ?? 0, Biota.AnglesY ?? 0, Biota.AnglesZ ?? 0, Biota.AnglesW ?? 0);

        // the following allows profile to offset from generators position, with no rotation changes, before then scattering from that position. Use case is mainly to spawn something higher or lower.

        if ((Biota.ObjCellId ?? 0) == 0) // if ObjCellId is specific, throw out that position (probably a linkable) and just use the generator's position else use the data as an offset. It is also possible that scatter always throws out all of it all cases.
        {
            obj.Location.PositionX += Biota.OriginX ?? 0;
            obj.Location.PositionY += Biota.OriginY ?? 0;
            obj.Location.PositionZ += Biota.OriginZ ?? 0;

            obj.Location.RotationW = Biota.AnglesW ?? 0.0f;
            obj.Location.RotationX = Biota.AnglesX ?? 0.0f;
            obj.Location.RotationY = Biota.AnglesY ?? 0.0f;
            obj.Location.RotationZ = Biota.AnglesZ ?? 0.0f;
        }

        obj.Location.PositionZ += 0.05f;

        // we are going to delay this scatter logic until the physics engine,
        // where the remnants of this function are in the client (SetScatterPositionInternal)

        // this is due to each randomized position being required to go through the full InitialPlacement process, to verify success
        // if InitialPlacement fails, then we retry up to maxTries

        obj.ScatterPos = new SetPosition(
            new Physics.Common.Position(obj.Location),
            SetPositionFlags.RandomScatter,
            genRadius
        );

        var success = obj.EnterWorld();

        obj.ScatterPos = null;

        return success;
    }

    public bool Spawn_Container(WorldObject obj)
    {
        var success = Generator is Container container && container.TryAddToInventory(obj);

        if (!success)
        {
            _log.Warning(
                "[GENERATOR] 0x{GeneratorGuid}:{GeneratorWeenieClassId} {GeneratorName}.Spawn_Container({WorldObjectName}) - failed to add to container inventory",
                Generator.Guid,
                Generator.WeenieClassId,
                Generator.Name,
                obj.Name
            );
        }

        return success;
    }

    public bool Spawn_Shop(WorldObject obj)
    {
        // spawn item in vendor shop inventory
        if (!(Generator is Vendor vendor))
        {
            _log.Warning(
                "[GENERATOR] 0x{GeneratorGuid}:{GeneratorWeenieClassId} {GeneratorName}.Spawn_Shop({WorldObjectName}) - generator is not a vendor type",
                Generator.Guid,
                Generator.WeenieClassId,
                Generator.Name,
                obj.Name
            );
            return false;
        }

        vendor.AddDefaultItem(obj);
        return true;
    }

    public bool Spawn_Default(WorldObject obj)
    {
        // default location handler?
        //log.DebugFormat("{0}.Spawn_Default({1}): default handler for RegenLocationType {2}", _generator.Name, obj.Name, RegenLocationType);

        obj.Location = new ACE.Entity.Position(Generator.Location);

        obj.Location.PositionZ += 0.05f;

        if (!VerifyLandblock(obj) || !VerifyWalkableSlope(obj))
        {
            return false;
        }

        return obj.EnterWorld();
    }

    public bool VerifyLandblock(WorldObject obj)
    {
        if (obj.Location == null || obj.Location.Landblock != Generator.Location.Landblock)
        {
            //log.DebugFormat("{0}.VerifyLandblock({1}) - spawn location is invalid landblock", _generator.Name, obj.Name);
            return false;
        }
        return true;
    }

    public bool VerifyWalkableSlope(WorldObject obj)
    {
        if (
            !obj.Location.Indoors
            && !obj.Location.IsWalkable()
            && !VerifyWalkableSlopeExcludedLandblocks.Contains(obj.Location.LandblockId.Landblock)
        )
        {
            //log.DebugFormat("{0}.VerifyWalkableSlope({1}) - spawn location is unwalkable slope", _generator.Name, obj.Name);
            return false;
        }
        return true;
    }

    /// <summary>
    /// A list of landblocks the excluded from VerifyWalkableSlope check
    ///
    /// TODO gmriggs
    /// Hack until this can be looked into more.
    /// </summary>
    public static HashSet<ushort> VerifyWalkableSlopeExcludedLandblocks = new HashSet<ushort>()
    {
        0x9EE5, // Northwatch Castle
        0xF92F, // Freebooter Keep
    };

    /// <summary>
    /// Generates a randomized treasure from LootGenerationFactory
    /// </summary>
    public List<WorldObject> TreasureGenerator(int? tier = null)
    {
        // profile.WeenieClassId is not a weenieClassId,
        // it's a DeathTreasure or WieldedTreasure table DID
        // there is no overlap of DIDs between these 2 tables,
        // so they can be searched in any order..
        var deathTreasure = LootGenerationFactory.GetTweakedDeathTreasureProfile(Biota.WeenieClassId, Generator);
        //Console.WriteLine(deathTreasure.MundaneItemChance);
        if (deathTreasure != null)
        {
            if (tier != null)
            {
                deathTreasure.Tier = (int)tier;
            }

            if (Generator.LootQualityMod.HasValue)
            {
                deathTreasure.LootQualityMod = (float)Generator.LootQualityMod;
            }

            // _log.Debug("{GeneratorName}.TreasureGenerator(): found death treasure {BiotaWcid}", Generator.Name, Biota.WeenieClassId);
            var generatedLoot = LootGenerationFactory.CreateRandomLootObjects(deathTreasure);

            if ((RegenLocationType & RegenLocationType.Contain) == 0) // If we're not a container make sure we respect our generate limit.
            {
                while (generatedLoot.Count > MaxCreate)
                {
                    var index = ThreadSafeRandom.Next(0, generatedLoot.Count - 1);
                    generatedLoot[index].DeleteObject();
                    generatedLoot.RemoveAt(index);
                }
            }

            return generatedLoot;
        }
        else
        {
            var wieldedTreasure = DatabaseManager.World.GetCachedWieldedTreasure(Biota.WeenieClassId);
            if (wieldedTreasure != null)
            {
                // TODO: get randomly generated wielded treasure from LootGenerationFactory
                //log.DebugFormat("{0}.TreasureGenerator(): found wielded treasure {1}", _generator.Name, Biota.WeenieClassId);

                // roll into the wielded treasure table
                //var table = new TreasureWieldedTable(wieldedTreasure);
                return WorldObject.GenerateWieldedTreasureSets(wieldedTreasure);
            }
            else
            {
                _log.Debug(
                    "[GENERATOR] 0x{GeneratorGuid}:{GeneratorWeenieClassId} {GeneratorName}.TreasureGenerator(): couldn't find death treasure or wielded treasure for ID {BiotaWeenieClassId}",
                    Generator.Guid,
                    Generator.WeenieClassId,
                    Generator.Name,
                    Biota.WeenieClassId
                );
                return null;
            }
        }
    }

    /// <summary>
    /// Removes all of the objects from a container for this profile
    /// </summary>
    public void RemoveTreasure()
    {
        var container = Generator as Container;
        if (container == null)
        {
            _log.Warning(
                "[GENERATOR] 0x{GeneratorGuid}:{GeneratorWeenieClassId} {GeneratorName}.RemoveTreasure(): container not found",
                Generator.Guid,
                Generator.WeenieClassId,
                Generator.Name
            );
            return;
        }
        foreach (var spawned in Spawned.Keys)
        {
            var inventoryObjGuid = new ObjectGuid(spawned);
            if (!container.Inventory.TryGetValue(inventoryObjGuid, out var inventoryObj))
            {
                _log.Warning(
                    "[GENERATOR] 0x{GeneratorGuid}:{GeneratorWeenieClassId} {GeneratorName}.RemoveTreasure(): couldn't find {ContainerObjectGuid}",
                    Generator.Guid,
                    Generator.WeenieClassId,
                    Generator.Name,
                    inventoryObjGuid
                );
                continue;
            }
            container.TryRemoveFromInventory(inventoryObjGuid);
            inventoryObj.Destroy();
        }
        Spawned.Clear();
    }

    /// <summary>
    /// Callback system for objects notifying their generators of events,
    /// ie. item pickup
    /// </summary>
    public void NotifyGenerator(ObjectGuid target, RegenerationType eventType)
    {
        //log.DebugFormat("{0}.NotifyGenerator({1:X8}, {2})", _generator.Name, target, eventType);

        Spawned.TryGetValue(target.Full, out var woi);

        if (woi == null)
        {
            return;
        }

        var adjEventType = eventType; // some generators use pickup when they mean to use destruction, some use destruction when they mean to use pickup. this data comes from 16py mostly and these issues are corrected below.
        var whenCreate = Biota.WhenCreate;
        var adjWhenCreate = Biota.WhenCreate;

        if (eventType == RegenerationType.PickUp && whenCreate == RegenerationType.Destruction)
        {
            adjEventType = RegenerationType.Destruction;
        }

        if (eventType == RegenerationType.Destruction && whenCreate == RegenerationType.PickUp)
        {
            adjEventType = RegenerationType.PickUp;
        }

        // If WhenCreate is Undef, assume it means Destruction (bad data)
        if (eventType == RegenerationType.Destruction && whenCreate == RegenerationType.Undef)
        {
            adjWhenCreate = RegenerationType.Destruction;
        }

        // If WhenCreate is Undef, assume it means Pickup (bad data)
        if (eventType == RegenerationType.PickUp && whenCreate == RegenerationType.Undef)
        {
            adjWhenCreate = RegenerationType.PickUp;
        }

        //if (eventType != adjEventType)
        //    _log.Warning($"0x{Generator.Guid}:{Generator.Name}({Generator.WeenieClassId}).GeneratorProfile[{LinkId}].NotifyGenerator: RegenerationType = {eventType.ToString()}, WhenCreate = {whenCreate.ToString()}, Using {adjEventType.ToString()} as RegenerationType instead");

        if (whenCreate != adjWhenCreate)
        {
            _log.Warning(
                "[GENERATOR] 0x{GeneratorGuid}:{GeneratorName}({GeneratorWeenieClassId}).GeneratorProfile[{LinkId}].NotifyGenerator: RegenerationType = {RenegerationType}, WhenCreate = {BiotaWhenCreate}, Using {AdjustedBiotaWhenCreate} as WhenCreate instead",
                Generator.Guid,
                Generator.Name,
                Generator.WeenieClassId,
                LinkId,
                eventType,
                whenCreate,
                adjWhenCreate
            );
        }

        if (adjWhenCreate != adjEventType)
        {
            return;
        }

        Spawned.Remove(woi.Guid.Full);

        NextAvailable = DateTime.UtcNow.AddSeconds(Delay);

        if (Generator.GetProperty(PropertyBool.IsPseudoRandomGenerator) == true)
        {
            Generator.GeneratorCooldown = Time.GetUnixTime();
        }
    }

    public bool GeneratorResetInProgress = false;

    public void Reset()
    {
        GeneratorResetInProgress = true;
        foreach (var rNode in Spawned.Values)
        {
            var wo = rNode.TryGetWorldObject();

            if (wo != null)
            {
                if (wo.IsGenerator)
                {
                    wo.ResetGenerator();
                }

                if (wo.Container == Generator)
                {
                    var container = Generator as Container;
                    if (container?.TryRemoveFromInventory(wo.Guid) ?? false)
                    {
                        wo.Destroy();
                    }
                }

                wo.Destroy();
            }
        }
        GeneratorResetInProgress = false;

        CleanupProfile();
    }

    public void KillAll()
    {
        foreach (var rNode in Spawned.Values)
        {
            var wo = rNode.TryGetWorldObject();

            if (wo is Creature creature && !creature.IsDead)
            {
                creature.Smite(Generator, true);
            }
        }

        CleanupProfile();
    }

    public void DestroyAll(bool fromLandblockUnload = false)
    {
        foreach (var rNode in Spawned.Values)
        {
            var wo = rNode.TryGetWorldObject();

            if (wo != null && (!(wo is Creature creature) || !creature.IsDead))
            {
                wo.Destroy(true, fromLandblockUnload);
            }
        }

        CleanupProfile();
    }

    public void StartAllContainersDecay()
    {
        foreach (var rNode in Spawned.Values)
        {
            var wo = rNode.TryGetWorldObject();
        }
    }

    private void CleanupProfile()
    {
        Spawned.Clear();
        SpawnQueue.Clear();

        NextAvailable = DateTime.UtcNow;

        GeneratedTreasureItem = false;
        Generator.GeneratedTreasureItem = false;
    }
}
