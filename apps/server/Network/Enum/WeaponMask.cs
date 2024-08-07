using System;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.Structure;

namespace ACE.Server.Network.Enum;

[Flags]
public enum WeaponMask
{
    AttackSkill = 0x1,
    MeleeDefense = 0x2,
    Speed = 0x4,
    Damage = 0x8,
    DamageVariance = 0x10,
    DamageMod = 0x20,
    Use = 0x40
}

public static class WeaponMaskHelper
{
    public static WeaponMask GetHighlightMask(WeaponProfile profile, bool highlightUse)
    {
        WeaponMask highlightMask = 0;

        var weapon = profile.Weapon;

        // Enchant applies to all weapons
        if (profile.Enchantment_WeaponDefense != 0)
        {
            highlightMask |= WeaponMask.MeleeDefense;
        }

        // Following enchants do not apply to caster weapons
        if (weapon.WeenieType != WeenieType.Caster)
        {
            if (profile.Enchantment_WeaponOffense != 0 && highlightUse)
            {
                highlightMask |= WeaponMask.Use;
            }

            if (profile.Enchantment_WeaponOffense != 0)
            {
                highlightMask |= WeaponMask.AttackSkill;
            }

            if (profile.Enchantment_WeaponTime != 0)
            {
                highlightMask |= WeaponMask.Speed;
            }

            if (profile.Enchantment_Damage != 1.0f && !weapon.IsAmmoLauncher)
            {
                highlightMask |= WeaponMask.Damage;
            }

            if (profile.Enchantment_DamageVariance != 1.0f)
            {
                highlightMask |= WeaponMask.DamageVariance;
            }
            //if (profile.Enchantment_DamageMod != 1.0f)
            //    highlightMask |= WeaponMask.DamageMod;
        }

        return highlightMask;
    }

    public static WeaponMask GetColorMask(WeaponProfile profile, bool highlightUse)
    {
        WeaponMask colorMask = 0;

        var weapon = profile.Weapon;

        // Enchant applies to all weapons
        if (profile.Enchantment_WeaponDefense > 0)
        {
            colorMask |= WeaponMask.MeleeDefense;
        }

        // Following enchants do not apply to caster weapons
        if (
            weapon.WeenieType != WeenieType.Caster
            && (weapon.WeenieType != WeenieType.Ammunition || PropertyManager.GetBool("show_ammo_buff").Item)
        )
        {
            // item enchantments are currently being cast on wielder
            if (profile.Enchantment_WeaponOffense > 0 && highlightUse)
            {
                colorMask |= WeaponMask.Use;
            }

            if (profile.Enchantment_WeaponOffense > 0)
            {
                colorMask |= WeaponMask.AttackSkill;
            }

            if (profile.Enchantment_WeaponTime < 0)
            {
                colorMask |= WeaponMask.Speed;
            }

            if (profile.Enchantment_Damage > 1.0f && !weapon.IsAmmoLauncher)
            {
                colorMask |= WeaponMask.Damage;
            }

            if (profile.Enchantment_DamageVariance < 1.0f)
            {
                colorMask |= WeaponMask.DamageVariance;
            }
            //if (profile.Enchantment_DamageMod > 1.0f)
            //    colorMask |= WeaponMask.DamageMod;
        }

        return colorMask;
    }
}
