using System;
using System.Collections.Generic;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using MotionCommand = ACE.Entity.Enum.MotionCommand;

namespace ACE.Server.WorldObjects;

public class TrophySolvent : Stackable
{
    private const uint EssenceWCID = 1053982;

    /// <summary>
    /// Maps trophy WCIDs to their essence properties (SpellDID, Use description)
    /// </summary>
    private static readonly Dictionary<uint, (uint? SpellDidCook, uint? SpellDidAlch, string Use)> TrophyEssenceMap = new Dictionary<uint, (uint?, uint?, string)>()
    {
        { 12345, (1234, 1234, "Use this essence to learn an ancient spell.") },
        { 67890, (null, 1234, "This essence contains pure power.") },
        { 11111, (5678, 1234, "Channel this essence to unlock hidden knowledge.") },
    };

    /// <summary>
    /// A new biota be created taking all of its values from weenie.
    /// </summary>
    public TrophySolvent(Weenie weenie, ObjectGuid guid)
        : base(weenie, guid)
    {
        SetEphemeralValues();
    }

    /// <summary>
    /// Restore a WorldObject from the database.
    /// </summary>
    public TrophySolvent(Biota biota)
        : base(biota)
    {
        SetEphemeralValues();
    }

    private void SetEphemeralValues() { }

    private static void BroadcastTrophyConversion(
        Player player,
        string trophyName,
        string essenceName,
        int numberOfSolventsConsumed,
        bool success
    )
    {
        // send local broadcast
        if (success)
        {
            player.EnqueueBroadcast(
                new GameMessageSystemChat(
                    $"You successfully convert {trophyName} into {essenceName}, consuming {numberOfSolventsConsumed} Trophy Solvents.",
                    ChatMessageType.Broadcast
                ),
                8f,
                ChatMessageType.Broadcast
            );
        }
        else
        {
            player.EnqueueBroadcast(
                new GameMessageSystemChat(
                    $"You fail to convert {trophyName}.",
                    ChatMessageType.Broadcast
                ),
                8f,
                ChatMessageType.Broadcast
            );
        }
    }

    public override void HandleActionUseOnTarget(Player player, WorldObject target)
    {
        UseObjectOnTarget(player, this, target);
    }

    public static void UseObjectOnTarget(Player player, WorldObject source, WorldObject target, bool confirmed = false)
    {
        var solventStackSize = source.StackSize ?? 1;

        if (player.IsBusy)
        {
            player.SendUseDoneEvent(WeenieError.YoureTooBusy);
            return;
        }

        if (target.WeenieType == source.WeenieType)
        {
            player.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements);
            return;
        }

        if (!RecipeManager.VerifyUse(player, source, target, true))
        {
            if (!confirmed)
            {
                player.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements);
            }
            else
            {
                player.SendTransientError(
                    "Either you or one of the items involved does not pass the requirements for this craft interaction."
                );
            }

            return;
        }

        // Check if target is a trophy
        var trophyQuality = target.TrophyQuality;
        if (trophyQuality == null || trophyQuality < 1 || trophyQuality > 10)
        {
            player.Session.Network.EnqueueSend(
                new GameMessageSystemChat(
                    $"The {target.NameWithMaterial} is not a valid trophy item.",
                    ChatMessageType.Craft
                )
            );
            player.SendUseDoneEvent();
            return;
        }

        if (target.Retained)
        {
            player.Session.Network.EnqueueSend(
                new GameMessageSystemChat(
                    $"The {target.NameWithMaterial} is retained and cannot be altered.",
                    ChatMessageType.Craft
                )
            );
            player.SendUseDoneEvent();
            return;
        }

        // Calculate amount of solvents needed based on trophy quality (1-10)
        // Using squared formula similar to SpellPurge's workmanship calculation
        var amountToConsume = trophyQuality.Value * trophyQuality.Value;

        if (solventStackSize < amountToConsume)
        {
            player.Session.Network.EnqueueSend(
                new GameMessageSystemChat(
                    $"You require a stack of {amountToConsume} Trophy Solvents to convert {target.NameWithMaterial}.",
                    ChatMessageType.Craft
                )
            );
            player.SendUseDoneEvent();
            return;
        }

        var essenceName = $"Essence of {target.Name}";

        if (!confirmed)
        {
            var confirmationMessage =
                $"Convert {target.NameWithMaterial} into {essenceName}?\n\n" +
                $"This will consume the trophy and {amountToConsume} Trophy Solvents.\n\n";

            if (!player.ConfirmationManager.EnqueueSend(new Confirmation_CraftInteration(player.Guid, source.Guid, target.Guid), confirmationMessage))
            {
                player.SendUseDoneEvent(WeenieError.ConfirmationInProgress);
            }

            if (PropertyManager.GetBool("craft_exact_msg").Item)
            {
                var exactMsg = $"You have a 100% chance of converting {target.NameWithMaterial} into essence.";

                player.Session.Network.EnqueueSend(new GameMessageSystemChat(exactMsg, ChatMessageType.Craft));
            }

            return;
        }

        var actionChain = new ActionChain();

        var animTime = 0.0f;

        player.IsBusy = true;

        if (player.CombatMode != CombatMode.NonCombat)
        {
            var stanceTime = player.SetCombatMode(CombatMode.NonCombat);
            actionChain.AddDelaySeconds(stanceTime);

            animTime += stanceTime;
        }

        animTime += player.EnqueueMotion(actionChain, MotionCommand.ClapHands);

        actionChain.AddAction(
            player,
            () =>
            {
                if (!RecipeManager.VerifyUse(player, source, target, true))
                {
                    player.SendTransientError(
                        "Either you or one of the items involved does not pass the requirements for this craft interaction."
                    );
                    return;
                }

                // Recalculate amount to consume to ensure consistency
                var trophyQualityValue = target.TrophyQuality ?? 1;
                var finalAmountToConsume = trophyQualityValue * trophyQualityValue;

                // Create the essence item
                var essence = CreateEssenceFromTrophy(target);
                if (essence == null)
                {
                    _log.Error("UseObjectOnTarget() - Failed to create essence from {Target}", target);
                    player.SendTransientError("Failed to create essence from trophy.");
                    return;
                }

                // Add essence to player's inventory
                if (!player.TryCreateInInventoryWithNetworking(essence))
                {
                    _log.Error("UseObjectOnTarget() - Failed to add essence to player inventory");
                    player.SendTransientError("Failed to add essence to inventory.");
                    essence.Destroy();
                    return;
                }

                // Remove the trophy and consume solvents
                player.TryConsumeFromInventoryWithNetworking(target, 1);
                player.TryConsumeFromInventoryWithNetworking(source, finalAmountToConsume);

                BroadcastTrophyConversion(player, target.NameWithMaterial, essenceName, finalAmountToConsume, true);
            }
        );

        player.EnqueueMotion(actionChain, MotionCommand.Ready);

        actionChain.AddAction(
            player,
            () =>
            {
                player.SendUseDoneEvent();
                player.IsBusy = false;
            }
        );

        actionChain.EnqueueChain();

        player.NextUseTime = DateTime.UtcNow.AddSeconds(animTime);
    }

    private static WorldObject CreateEssenceFromTrophy(WorldObject trophy)
    {
        var essence = WorldObjectFactory.CreateNewWorldObject(EssenceWCID);

        if (essence == null)
        {
            _log.Error("CreateEssenceFromTrophy() - Failed to create essence with WCID {EssenceWCID}", EssenceWCID);
            return null;
        }

        // Set the essence name
        essence.Name = $"Essence of {trophy.Name}";

        // Transfer the trophy's icon to the essence (IconUnderlayId remains unchanged from the base essence)
        if (trophy.IconId != 0)
        {
            essence.SetProperty(PropertyDataId.Icon, trophy.IconId);
        }

        // Transfer the trophy quality
        if (trophy.TrophyQuality.HasValue)
        {
            essence.SetProperty(PropertyInt.TrophyQuality, trophy.TrophyQuality.Value);
        }

        // Transfer the trophy value
        if (trophy.Value.HasValue)
        {
            essence.SetProperty(PropertyInt.Value, trophy.Value.Value);
        }

        if (TrophyEssenceMap.TryGetValue(trophy.WeenieClassId, out var essenceData))
        {
            // Set Cooking Spell Id if present
            if (essenceData.SpellDidCook.HasValue && essenceData.SpellDidCook.Value != 0)
            {
                essence.SetProperty(PropertyInt.TrophyEssenceSpellIdCook, (int)essenceData.SpellDidCook.Value);
            }

            // Set Alchemy Spell Id if present
            if (essenceData.SpellDidAlch.HasValue && essenceData.SpellDidAlch.Value != 0)
            {
                essence.SetProperty(PropertyInt.TrophyEssenceSpellIdAlch, (int)essenceData.SpellDidAlch.Value);
            }

            // Set Use description if present
            if (!string.IsNullOrEmpty(essenceData.Use))
            {
                essence.SetProperty(PropertyString.Use, essenceData.Use);
            }
        }

        return essence;
    }

    public int? TrophyEssenceSpellIdCook
    {
        get => (int?)GetProperty(PropertyInt.TrophyEssenceSpellIdCook);
        set
        {
            if (!value.HasValue)
            {
                RemoveProperty(PropertyInt.TrophyEssenceSpellIdCook);
            }
            else
            {
                SetProperty(PropertyInt.TrophyEssenceSpellIdCook, value.Value);
            }
        }
    }

    public int? TrophyEssenceSpellIdAlch
    {
        get => (int?)GetProperty(PropertyInt.TrophyEssenceSpellIdAlch);
        set
        {
            if (!value.HasValue)
            {
                RemoveProperty(PropertyInt.TrophyEssenceSpellIdAlch);
            }
            else
            {
                SetProperty(PropertyInt.TrophyEssenceSpellIdAlch, value.Value);
            }
        }
    }
}
