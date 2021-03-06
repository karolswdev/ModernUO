namespace Server.Items
{
    public class JocklesQuicksword : Longsword
    {
        [Constructible]
        public JocklesQuicksword()
        {
            LootType = LootType.Blessed;

            Attributes.AttackChance = 5;
            Attributes.WeaponSpeed = 10;
            Attributes.WeaponDamage = 25;
        }

        public JocklesQuicksword(Serial serial) : base(serial)
        {
        }

        public override int LabelNumber => 1077666; // Jockles' Quicksword

        public override void Serialize(IGenericWriter writer)
        {
            base.Serialize(writer);

            writer.WriteEncodedInt(0); // version
        }

        public override void Deserialize(IGenericReader reader)
        {
            base.Deserialize(reader);

            var version = reader.ReadEncodedInt();
        }
    }
}
