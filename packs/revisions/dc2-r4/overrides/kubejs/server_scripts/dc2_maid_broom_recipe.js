// priority: -100
// DC2 r4: replace the forbidden ender eye without exceeding the six altar slots.
// The extra crafting step preserves all original hay, stick and power costs.
ServerEvents.recipes(event => {
    const focusNbt = '{dc2_broom_focus:1b,display:{Name:\'{"text":"扫帚祭坛材料","italic":false,"color":"gold"}\',Lore:[\'{"text":"末影珍珠 + 烈焰粉，用于女仆扫帚祭坛配方","italic":false,"color":"gray"}\']}}';
    event.shapeless(Item.of('minecraft:blaze_powder', focusNbt), [
        'minecraft:ender_pearl',
        'minecraft:blaze_powder'
    ]).id('deceasedcraft2:broom_altar_material');

    event.remove({id: 'touhou_little_maid:altar/craft_broom'});
    event.custom({
        type: 'touhou_little_maid:altar_crafting',
        output: {type: 'minecraft:item', nbt: {Item: {id: 'touhou_little_maid:broom', Count: 1}}},
        power: 0.2,
        ingredients: [
            {item: 'minecraft:hay_block'},
            {item: 'minecraft:hay_block'},
            {item: 'minecraft:hay_block'},
            {tag: 'forge:rods/wooden'},
            {tag: 'forge:rods/wooden'},
            {type: 'forge:nbt', item: 'minecraft:blaze_powder', count: 1, nbt: focusNbt}
        ]
    }).id('touhou_little_maid:altar/craft_broom');
    console.info('[DC2] Registered broom altar recipe: ender pearl + blaze powder focus; six altar slots; power 0.2');
});
