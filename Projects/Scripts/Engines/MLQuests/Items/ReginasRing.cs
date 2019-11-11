namespace Server.Items
{
  public class ReginasRing : SilverRing
  {
    [Constructible]
    public ReginasRing() => LootType = LootType.Blessed;

    public ReginasRing(Serial serial) : base(serial)
    {
    }

    public override int LabelNumber => 1075305; // Regina's Ring

    public override bool Nontransferable => true;

    public override void AddNameProperties(ObjectPropertyList list)
    {
      base.AddNameProperties(list);
      AddQuestItemProperty(list);
    }

    public override void Serialize(GenericWriter writer)
    {
      base.Serialize(writer);

      writer.Write(0); // Version
    }

    public override void Deserialize(GenericReader reader)
    {
      base.Deserialize(reader);

      int version = reader.ReadInt();
    }
  }
}