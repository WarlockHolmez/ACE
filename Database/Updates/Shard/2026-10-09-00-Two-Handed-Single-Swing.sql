/*
 * Two-handed weapons swing once.
 *
 * The two-handed attack animations now strike once instead of twice, so two-handed weapons get new
 * WeaponTimes: 68 for axes and maces, 65 for swords, 60 for spears (the loot weapons). Other two-handed
 * weapons keep their speed relative to those. Damage is scaled by the same ratio as WeaponTime, so damage
 * per second is unchanged. world-db changed the weapons, which only affects NEWLY created items.
 *
 * This script retroactively converts already-persisted two-handed weapons (WeaponTime, Damage,
 * BaseWeaponTime, BaseDamage) to match. Each weapon's WeaponTime is multiplied by its group's new
 * WeaponTime over the group's old one (the loot roll on it is kept), and its Damage by its new WeaponTime
 * over its old one. The group comes from the weenie it was made from, since loot two-handed weapons all
 * have the Two-Handed Combat skill.
 *
 * Run 2026-10-07-00-Weapon-Time-Rework.sql first. RUN THIS ONCE: running it again scales the weapons again.
 * Back up the shard database before applying this.
 */

-- Preview: how many two-handed weapons will be converted
-- SELECT COUNT(*) FROM biota_properties_int WHERE `type` = 46 AND value = 8;

START TRANSACTION;

DROP TEMPORARY TABLE IF EXISTS two_handed_group;

CREATE TEMPORARY TABLE two_handed_group (
    weenie_Class_Id INT UNSIGNED NOT NULL PRIMARY KEY,
    new_weapon_time INT NOT NULL,
    old_weapon_time INT NOT NULL
);

/* (weenie, the group's new WeaponTime, the group's old WeaponTime) for every two-handed weapon in world-db */
INSERT INTO two_handed_group (weenie_Class_Id, new_weapon_time, old_weapon_time) VALUES
    (6665, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6666, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6667, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6668, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6676, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6677, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6678, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6679, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6680, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6681, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6682, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6691, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6692, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6693, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6694, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6702, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6703, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6704, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6705, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6706, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6707, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6708, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6717, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6718, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6719, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6720, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6728, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6729, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6730, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6731, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6732, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6733, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6734, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6743, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6744, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6745, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6746, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6754, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6755, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6756, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6757, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6758, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6759, 68, 95) /* axe: Silifi of Crimson Stars */,
    (6760, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22953, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22957, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22961, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22962, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22963, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22967, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22968, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22969, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22973, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22974, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22975, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22979, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22983, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22987, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22988, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22989, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22993, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22994, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22995, 68, 95) /* axe: Silifi of Crimson Stars */,
    (22999, 68, 95) /* axe: Silifi of Crimson Stars */,
    (23000, 68, 95) /* axe: Silifi of Crimson Stars */,
    (23001, 68, 95) /* axe: Silifi of Crimson Stars */,
    (23005, 68, 95) /* axe: Silifi of Crimson Stars */,
    (23009, 68, 95) /* axe: Silifi of Crimson Stars */,
    (23013, 68, 95) /* axe: Silifi of Crimson Stars */,
    (23014, 68, 95) /* axe: Silifi of Crimson Stars */,
    (23015, 68, 95) /* axe: Silifi of Crimson Stars */,
    (23019, 68, 95) /* axe: Silifi of Crimson Stars */,
    (23020, 68, 95) /* axe: Silifi of Crimson Stars */,
    (23021, 68, 95) /* axe: Silifi of Crimson Stars */,
    (23025, 68, 95) /* axe: Silifi of Crimson Stars */,
    (23026, 68, 95) /* axe: Silifi of Crimson Stars */,
    (23027, 68, 95) /* axe: Silifi of Crimson Stars */,
    (40618, 65, 83) /* sword: Greatsword */,
    (40619, 65, 83) /* sword: Acid Greatsword */,
    (40620, 65, 83) /* sword: Lightning Greatsword */,
    (40621, 65, 83) /* sword: Flaming Greatsword */,
    (40622, 65, 83) /* sword: Frost Greatsword */,
    (40623, 68, 95) /* mace: Quadrelle */,
    (40624, 68, 95) /* mace: Acid Quadrelle */,
    (40625, 68, 95) /* mace: Lightning Quadrelle */,
    (40626, 68, 95) /* mace: Flaming Quadrelle */,
    (40627, 68, 95) /* mace: Frost Quadrelle */,
    (40635, 68, 95) /* mace: Tetsubo */,
    (40636, 68, 95) /* mace: Acid Tetsubo */,
    (40637, 68, 95) /* mace: Lightning Tetsubo */,
    (40638, 68, 95) /* mace: Flaming Tetsubo */,
    (40639, 68, 95) /* mace: Frost Tetsubo */,
    (40760, 65, 83) /* sword: Nodachi */,
    (40761, 65, 83) /* sword: Acid Nodachi */,
    (40762, 65, 83) /* sword: Lightning Nodachi */,
    (40763, 65, 83) /* sword: Flaming Nodachi */,
    (40764, 65, 83) /* sword: Frost Nodachi */,
    (40818, 60, 83) /* spear: Corsesca */,
    (40819, 60, 83) /* spear: Acid Corsesca */,
    (40820, 60, 83) /* spear: Lightning Corsesca */,
    (40821, 60, 83) /* spear: Flaming Corsesca */,
    (40822, 60, 83) /* spear: Frost Corsesca */,
    (41036, 60, 83) /* spear: Assagai */,
    (41037, 60, 83) /* spear: Acid Assagai */,
    (41038, 60, 83) /* spear: Lightning Assagai */,
    (41039, 60, 83) /* spear: Flaming Assagai */,
    (41040, 60, 83) /* spear: Frost Assagai */,
    (41041, 60, 95) /* magari: Magari Yari */,
    (41042, 60, 95) /* magari: Acid Magari Yari */,
    (41043, 60, 95) /* magari: Lightning Magari Yari */,
    (41044, 60, 95) /* magari: Flaming Magari Yari */,
    (41045, 60, 95) /* magari: Frost Magari Yari */,
    (41046, 60, 83) /* spear: Pike */,
    (41047, 60, 83) /* spear: Acid Pike */,
    (41048, 60, 83) /* spear: Lightning Pike */,
    (41049, 60, 83) /* spear: Flaming Pike */,
    (41050, 60, 83) /* spear: Frost Pike */,
    (41052, 68, 95) /* axe: Greataxe */,
    (41053, 68, 95) /* axe: Acid Greataxe */,
    (41054, 68, 95) /* axe: Lightning Greataxe */,
    (41055, 68, 95) /* axe: Flaming Greataxe */,
    (41056, 68, 95) /* axe: Frost Greataxe */,
    (41057, 68, 95) /* mace: Great Star Mace */,
    (41058, 68, 95) /* mace: Acid Great Star Mace */,
    (41059, 68, 95) /* mace: Lightning Great Star Mace */,
    (41060, 68, 95) /* mace: Flaming Great Star Mace */,
    (41061, 68, 95) /* mace: Frost Great Star Mace */,
    (41062, 68, 95) /* mace: Khanda-handled Mace */,
    (41063, 68, 95) /* mace: Acid Khanda-handled Mace */,
    (41064, 68, 95) /* mace: Lightning Khanda-handled Mace */,
    (41065, 68, 95) /* mace: Flaming Khanda-handled Mace */,
    (41066, 68, 95) /* mace: Frost Khanda-handled Mace */,
    (41067, 65, 83) /* sword: Shashqa */,
    (41068, 65, 83) /* sword: Acid Shashqa */,
    (41069, 65, 83) /* sword: Lightning Shashqa */,
    (41070, 65, 83) /* sword: Flaming Shashqa */,
    (41071, 65, 83) /* sword: Frost Shashqa */,
    (1000304, 68, 95) /* axe: Greataxe (0) */,
    (1000305, 68, 95) /* axe: Greataxe (1) */,
    (1000306, 68, 95) /* axe: Greataxe (2) */,
    (1000307, 68, 95) /* axe: Greataxe (3) */,
    (1000308, 68, 95) /* axe: Greataxe (4) */,
    (1000309, 68, 95) /* axe: Greataxe (5) */,
    (1000310, 68, 95) /* axe: Greataxe (6) */,
    (1000311, 68, 95) /* axe: Greataxe (7) */,
    (1000312, 65, 83) /* sword: Greatsword (0) */,
    (1000313, 65, 83) /* sword: Greatsword (1) */,
    (1000314, 65, 83) /* sword: Greatsword (2) */,
    (1000315, 65, 83) /* sword: Greatsword (3) */,
    (1000316, 65, 83) /* sword: Greatsword (4) */,
    (1000317, 65, 83) /* sword: Greatsword (5) */,
    (1000318, 65, 83) /* sword: Greatsword (6) */,
    (1000319, 65, 83) /* sword: Greatsword (7) */,
    (1000320, 60, 83) /* spear: Assagai (0) */,
    (1000321, 60, 83) /* spear: Assagai (1) */,
    (1000322, 60, 83) /* spear: Assagai (2) */,
    (1000323, 60, 83) /* spear: Assagai (3) */,
    (1000324, 60, 83) /* spear: Assagai (4) */,
    (1000325, 60, 83) /* spear: Assagai (5) */,
    (1000326, 60, 83) /* spear: Assagai (6) */,
    (1000327, 60, 83) /* spear: Assagai (7) */,
    (1031043, 65, 83) /* sword: Nodachi */,
    (1031430, 60, 83) /* spear: Pike */,
    (1031781, 60, 83) /* spear: Rod of Darkness */,
    (1032038, 68, 95) /* mace: unnamed */,
    (1032039, 68, 95) /* mace: unnamed */,
    (1032046, 60, 83) /* spear: Rod of Darkness */,
    (1033302, 68, 95) /* mace: unnamed */,
    (1033498, 65, 83) /* sword: Claymore */,
    (1050946, 65, 83) /* sword: Academy Greatsword */,
    (1050947, 65, 83) /* sword: Academy Greatsword */,
    (1050959, 65, 83) /* sword: Academy Greatsword */,
    (1054204, 65, 83) /* sword: Warden's Greatsword */,
    (2036451, 68, 95) /* mace: Lai Lin's Great Mace */,
    (2036551, 60, 83) /* spear: Merewin's Pike */;

DROP TEMPORARY TABLE IF EXISTS two_handed_rework;

CREATE TEMPORARY TABLE two_handed_rework (
    object_Id INT UNSIGNED NOT NULL PRIMARY KEY,
    scale DOUBLE NOT NULL,
    weapon_time INT NULL,
    new_weapon_time INT NULL,
    base_weapon_time INT NULL,
    new_base_weapon_time INT NULL
);

INSERT INTO two_handed_rework (object_Id, scale, weapon_time, base_weapon_time)
SELECT b.id, g.new_weapon_time / CAST(g.old_weapon_time AS DOUBLE), wt.value, bwt.value
FROM biota b
JOIN two_handed_group g ON g.weenie_Class_Id = b.weenie_Class_Id
JOIN biota_properties_int style ON style.object_Id = b.id AND style.`type` = 46 /* DefaultCombatStyle */ AND style.value = 8 /* TwoHanded */
LEFT JOIN biota_properties_int wt ON wt.object_Id = b.id AND wt.`type` = 49 /* WeaponTime */
LEFT JOIN biota_properties_int bwt ON bwt.object_Id = b.id AND bwt.`type` = 454 /* BaseWeaponTime */
WHERE b.weenie_Type = 6 /* MeleeWeapon */;

/* new WeaponTimes, rounded half up */
UPDATE two_handed_rework r SET
    r.new_weapon_time = CASE WHEN r.weapon_time > 0 THEN GREATEST(1, FLOOR(r.weapon_time * r.scale + 0.5e0)) ELSE r.weapon_time END,
    r.new_base_weapon_time = CASE WHEN r.base_weapon_time > 0 THEN GREATEST(1, FLOOR(r.base_weapon_time * r.scale + 0.5e0)) ELSE r.base_weapon_time END;

/* Damage by the WeaponTime ratio, so damage per second is unchanged */
UPDATE biota_properties_int p
JOIN two_handed_rework r ON r.object_Id = p.object_Id
SET p.value = FLOOR(p.value * r.new_weapon_time / CAST(r.weapon_time AS DOUBLE) + 0.5e0)
WHERE p.`type` = 44 /* Damage */ AND r.weapon_time > 0;

UPDATE biota_properties_int p
JOIN two_handed_rework r ON r.object_Id = p.object_Id
SET p.value = FLOOR(p.value * r.new_base_weapon_time / CAST(r.base_weapon_time AS DOUBLE) + 0.5e0)
WHERE p.`type` = 452 /* BaseDamage */ AND r.base_weapon_time > 0;

UPDATE biota_properties_int p
JOIN two_handed_rework r ON r.object_Id = p.object_Id
SET p.value = r.new_weapon_time
WHERE p.`type` = 49 /* WeaponTime */ AND r.weapon_time > 0;

UPDATE biota_properties_int p
JOIN two_handed_rework r ON r.object_Id = p.object_Id
SET p.value = r.new_base_weapon_time
WHERE p.`type` = 454 /* BaseWeaponTime */ AND r.base_weapon_time > 0;

DROP TEMPORARY TABLE two_handed_rework;
DROP TEMPORARY TABLE two_handed_group;

COMMIT;
