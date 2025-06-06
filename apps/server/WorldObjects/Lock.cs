using System;
using System.Diagnostics;
using ACE.Common;
using ACE.Common.Extensions;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using Serilog;

namespace ACE.Server.WorldObjects;

public enum UnlockResults : ushort
{
    UnlockSuccess = 0,
    PickLockFailed = 1,
    IncorrectKey = 2,
    AlreadyUnlocked = 3,
    CannotBePicked = 4,
    Open = 5
}

public interface Lock
{
    UnlockResults Unlock(uint unlockerGuid, Key key, string keyCode = null);
    UnlockResults Unlock(uint unlockerGuid, uint playerLockpickSkillLvl, ref int difficulty);
}

public class UnlockerHelper
{
    private static readonly ILogger _log = Log.ForContext(typeof(UnlockerHelper));

    public static string GetConsumeUnlockerMessage(Player player, int structure, bool isLockpick)
    {
        var msg = "";
        if (structure >= 0)
        {
            msg += $"Your {(isLockpick ? "lockpicks" : "key")} ";

            if (structure == 0)
            {
                msg += $"{(isLockpick ? "are" : "is")} used up.";
            }
            else
            {
                msg += $"{(isLockpick ? "have" : "has")} {structure} use{(structure > 1 ? "s" : "")} left.";
            }
        }

        return msg;
    }

    public static void SendUnlockResultMessage(
        Player player,
        int structure,
        bool isLockpick,
        WorldObject target,
        bool success
    )
    {
        var msg = "";
        if (isLockpick)
        {
            if (success)
            {
                msg = "You have successfully picked the lock!  It is now unlocked.\n ";
            }
            else
            {
                msg = "You have failed to pick the lock.  It is still locked.  ";
            }
        }
        else if (success)
        {
            msg = $"The {target.Name} has been unlocked.\n";
        }
        else
        {
            msg = $"The {target.Name} is still locked.\n";
        }

        msg += GetConsumeUnlockerMessage(player, structure, isLockpick);

        player.Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));
    }

    public static void SendDisarmResultMessage(Player player, int unlockerStructure, WorldObject target, bool success)
    {
        var msg = "";
        if (success)
        {
            msg = $"You have successfully disarmed the {target.Name}!  It is now disabled.\n ";
        }
        else
        {
            msg = $"You have failed to disarm the {target.Name}.  It is still active.  ";
        }

        msg += GetConsumeUnlockerMessage(player, unlockerStructure, true);

        player.Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));
    }

    public static int ConsumeUnlocker(Player player, WorldObject unlocker, WorldObject target)
    {
        // is Sonic Screwdriver supposed to be consumed on use?
        // it doesn't have a Structure, and it doesn't have PropertyBool.UnlimitedUse

        var unlimitedUses = unlocker.Structure == null || (unlocker.GetProperty(PropertyBool.UnlimitedUse) ?? false);

        var structure = -1;
        if (!unlimitedUses)
        {
            if (unlocker.Structure > 0)
            {
                unlocker.Structure--;
            }
            else
            {
                unlocker.Structure = 0;
            }

            structure = unlocker.Structure ?? 0;

            unlocker.Value -= unlocker.StructureUnitValue;

            if (unlocker.Value < 0) // fix negative value
            {
                unlocker.Value = 0;
            }

            if (unlocker.Structure == 0)
            {
                if (!player.TryConsumeFromInventoryWithNetworking(unlocker, 1))
                {
                    _log.Warning(
                        $"UnlockerHelper.ConsumeUnlocker: TryConsumeFromInventoryWithNetworking failed for {unlocker.Name} (0x{unlocker.Guid}:{unlocker.WeenieClassId}), used on {target.Name} (0x{target.Guid}:{target.WeenieClassId}) and used by {player.Name} (0x{player.Guid})"
                    );
                }
            }
            else
            {
                player.Session.Network.EnqueueSend(
                    new GameMessagePublicUpdatePropertyInt(unlocker, PropertyInt.Structure, (int)unlocker.Structure)
                );
            }
        }
        player.SendUseDoneEvent();

        return structure;
    }

    public static uint GetEffectiveLockpickSkill(Player player, WorldObject unlocker)
    {
        var lockpickSkill = player.GetCreatureSkill(Skill.Thievery).Current;

        var additiveBonus = unlocker.GetProperty(PropertyInt.LockpickMod) ?? 0;
        var multiplicativeBonus = unlocker.GetProperty(PropertyFloat.LockpickMod) ?? 1.0f;

        // is this really 10x bonus, or +10% bonus?
        if (multiplicativeBonus > 1.0f)
        {
            multiplicativeBonus = 1.0f + multiplicativeBonus * 0.01f;
        }

        var effectiveSkill = (int)Math.Round(lockpickSkill * multiplicativeBonus + additiveBonus);

        effectiveSkill = Math.Max(0, effectiveSkill);

        //Console.WriteLine($"Base skill: {lockpickSkill}");
        //Console.WriteLine($"Effective skill: {effectiveSkill}");

        return (uint)effectiveSkill;
    }

    public static void UseUnlocker(Player player, WorldObject unlocker, WorldObject target)
    {
        var chain = new ActionChain();

        chain.AddAction(
            player,
            () =>
            {
                if (
                    unlocker.WeenieType == WeenieType.Lockpick
                    && player.Skills[Skill.Thievery].AdvancementClass != SkillAdvancementClass.Trained
                    && player.Skills[Skill.Thievery].AdvancementClass != SkillAdvancementClass.Specialized
                )
                {
                    player.Session.Network.EnqueueSend(
                        new GameEventUseDone(player.Session, WeenieError.YouArentTrainedInLockpicking)
                    );
                    return;
                }
                if (target is Lock @lock)
                {
                    var result = UnlockResults.IncorrectKey;
                    var difficulty = 0;
                    if (unlocker.WeenieType == WeenieType.Lockpick)
                    {
                        var effectiveLockpickSkill = GetEffectiveLockpickSkill(player, unlocker);
                        result = @lock.Unlock(player.Guid.Full, effectiveLockpickSkill, ref difficulty);
                    }
                    else if (unlocker is Key woKey)
                    {
                        if (target is Door woDoor)
                        {
                            if (woDoor.LockCode == "") // the door isn't to be opened with keys
                            {
                                player.Session.Network.EnqueueSend(
                                    new GameEventUseDone(player.Session, WeenieError.YouCannotLockOrUnlockThat)
                                );
                                return;
                            }
                        }
                        result = @lock.Unlock(player.Guid.Full, woKey);
                    }

                    var isLockpick = unlocker.WeenieType == WeenieType.Lockpick;
                    switch (result)
                    {
                        case UnlockResults.UnlockSuccess:

                            if (unlocker.WeenieType == WeenieType.Lockpick)
                            {
                                // the source guid for this sound must be the player, else the sound will not play
                                // which differs from PicklockFail and LockSuccess being in the target sound table
                                player.EnqueueBroadcast(new GameMessageSound(player.Guid, Sound.Lockpicking, 1.0f));

                                var lockpickSkill = player.GetCreatureSkill(Skill.Thievery);
                                Proficiency.OnSuccessUse(player, lockpickSkill, difficulty);

                                // SPEC BONUS - Thievery: Up to 50% chance to receive a loot quality bonus when successfully picking a locked chest (25% bonus towards "remaining" loot quality mod
                                if (target.WeenieType == WeenieType.Chest)
                                {
                                    var thievery = player.GetCreatureSkill(Skill.Thievery);
                                    if (thievery.AdvancementClass == SkillAdvancementClass.Specialized)
                                    {
                                        var chest = target as Chest;
                                        if (chest != null)
                                        {
                                            var lockDifficulty = LockHelper.GetResistLockpick(target);

                                            var effectiveLockpickSkill = GetEffectiveLockpickSkill(player, unlocker);
                                            var skillCheck = (float)effectiveLockpickSkill / lockDifficulty;
                                            var lootQualityBonusCheck = skillCheck > 1f ? 0.5f : skillCheck * 0.5f;

                                            if (lootQualityBonusCheck > ThreadSafeRandom.Next(0f, 1f))
                                            {
                                                var baseLootQualityMod = chest.LootQualityMod;
                                                const float bonus = 0.25f;

                                                if (chest.LootQualityMod != null)
                                                {
                                                    chest.LootQualityMod += bonus;
                                                }
                                                else
                                                {
                                                    chest.LootQualityMod = bonus;
                                                }

                                                chest.Reset();

                                                chest.LootQualityMod = baseLootQualityMod;

                                                player.Session.Network.EnqueueSend(
                                                    new GameMessageSystemChat(
                                                        $"Your thievery skill allowed you to find higher quality loot!",
                                                        ChatMessageType.Broadcast
                                                    )
                                                );
                                            }
                                        }
                                    }
                                }
                            }
                            SendUnlockResultMessage(
                                player,
                                ConsumeUnlocker(player, unlocker, target),
                                isLockpick,
                                target,
                                true
                            );
                            break;

                        case UnlockResults.Open:
                            player.Session.Network.EnqueueSend(
                                new GameEventUseDone(player.Session, WeenieError.YouCannotLockWhatIsOpen)
                            );
                            break;
                        case UnlockResults.AlreadyUnlocked:
                            player.Session.Network.EnqueueSend(
                                new GameEventUseDone(player.Session, WeenieError.LockAlreadyUnlocked)
                            );
                            break;
                        case UnlockResults.PickLockFailed:
                            target.EnqueueBroadcast(new GameMessageSound(target.Guid, Sound.PicklockFail, 1.0f));
                            SendUnlockResultMessage(
                                player,
                                ConsumeUnlocker(player, unlocker, target),
                                isLockpick,
                                target,
                                false
                            );
                            break;
                        case UnlockResults.CannotBePicked:
                            player.Session.Network.EnqueueSend(
                                new GameEventUseDone(player.Session, WeenieError.YouCannotLockOrUnlockThat)
                            );
                            break;
                        case UnlockResults.IncorrectKey:
                            player.Session.Network.EnqueueSend(
                                new GameEventUseDone(player.Session, WeenieError.KeyDoesntFitThisLock)
                            );
                            break;
                    }
                }
                else
                {
                    player.Session.Network.EnqueueSend(
                        new GameEventUseDone(player.Session, WeenieError.YouCannotLockOrUnlockThat)
                    );
                }
            }
        );

        chain.EnqueueChain();
    }
}

public class LockHelper
{
    /// <summary>
    /// Returns TRUE if wo is a lockable item that can be picked
    /// </summary>
    public static bool IsPickable(WorldObject wo)
    {
        if (!(wo is Lock))
        {
            return false;
        }

        var resistLockpick = wo.ResistLockpick;

        // TODO: find out if ResistLockpick >= 9999 is a special 'unpickable' value in acclient,
        // similar to ResistMagic >= 9999 being equivalent to Unenchantable?

        if (resistLockpick == null || resistLockpick >= 9999)
        {
            return false;
        }

        return true;
    }

    public static int? GetResistLockpick(WorldObject wo)
    {
        if (!(wo is Lock))
        {
            return null;
        }

        // if base ResistLockpick without enchantments is unpickable,
        // do not apply enchantments
        var isPickable = IsPickable(wo);

        if (!isPickable)
        {
            return wo.ResistLockpick;
        }

        var resistLockpick = wo.ResistLockpick.Value;
        var enchantmentMod = wo.EnchantmentManager.GetResistLockpick();

        var difficulty = resistLockpick + enchantmentMod;

        // minimum 0 difficulty
        difficulty = Math.Max(0, difficulty);

        return difficulty;
    }

    public static string GetLockCode(WorldObject me)
    {
        string myLockCode = null;
        if (me is Door woDoor)
        {
            myLockCode = woDoor.LockCode;
        }
        else if (me is Chest woChest)
        {
            myLockCode = woChest.LockCode;
        }

        return myLockCode;
    }

    public static UnlockResults Unlock(WorldObject target, Key key, string keyCode = null)
    {
        if (keyCode == null)
        {
            keyCode = key?.KeyCode;
        }

        var myLockCode = GetLockCode(target);
        if (myLockCode == null)
        {
            return UnlockResults.IncorrectKey;
        }

        if (target.IsOpen)
        {
            return UnlockResults.Open;
        }

        // there is only 1 instance of an 'opens all' key in PY16 data, 'keysonicscrewdriver'
        // which uses keyCode '_bohemund's_magic_key_'

        // when LSD added the rare skeleton key (keyrarevolatileuniversal),
        // they used PropertyBool.OpensAnyLock, which appears to have been used for something else in retail on Writables:

        // https://github.com/ACEmulator/ACE-World-16PY/blob/master/Database/3-Core/9%20WeenieDefaults/SQL/Key/Key/09181%20Sonic%20Screwdriver.sql
        // https://github.com/ACEmulator/ACE-World-16PY/search?q=OpensAnyLock

        if (
            keyCode != null
                && (
                    keyCode.Equals(myLockCode, StringComparison.OrdinalIgnoreCase)
                    || keyCode.Equals("_bohemund's_magic_key_")
                )
            || key != null && key.OpensAnyLock
        )
        {
            if (!target.IsLocked)
            {
                return UnlockResults.AlreadyUnlocked;
            }

            target.IsLocked = false;
            var updateProperty = new GameMessagePublicUpdatePropertyBool(target, PropertyBool.Locked, target.IsLocked);
            var sound = new GameMessageSound(target.Guid, Sound.LockSuccess, 1.0f);
            target.EnqueueBroadcast(updateProperty, sound);
            return UnlockResults.UnlockSuccess;
        }
        return UnlockResults.IncorrectKey;
    }

    public static UnlockResults Unlock(WorldObject target, uint playerLockpickSkillLvl, ref int difficulty)
    {
        var isPickable = IsPickable(target);

        if (!isPickable)
        {
            return UnlockResults.CannotBePicked;
        }

        var myResistLockpick = GetResistLockpick(target);

        difficulty = myResistLockpick.Value;

        if (target.IsOpen)
        {
            return UnlockResults.Open;
        }

        if (!target.IsLocked)
        {
            return UnlockResults.AlreadyUnlocked;
        }

        var pickChance = SkillCheck.GetSkillChance((int)playerLockpickSkillLvl, difficulty);

#if DEBUG
        Debug.WriteLine($"{pickChance.FormatChance()} chance of UnlockSuccess");
#endif

        var dice = ThreadSafeRandom.Next(0.0f, 1.0f);
        if (dice >= pickChance)
        {
            return UnlockResults.PickLockFailed;
        }

        target.IsLocked = false;
        target.EnqueueBroadcast(new GameMessagePublicUpdatePropertyBool(target, PropertyBool.Locked, target.IsLocked));
        //target.CurrentLandblock?.EnqueueBroadcastSound(target, Sound.Lockpicking);
        return UnlockResults.UnlockSuccess;
    }
}
