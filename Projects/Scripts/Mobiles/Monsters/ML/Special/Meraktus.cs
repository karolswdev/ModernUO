using System;
using Server.Engines.CannedEvil;
using Server.Items;

namespace Server.Mobiles
{
  public class Meraktus : BaseChampion
  {
    [Constructible]
    public Meraktus()
      : base(AIType.AI_Melee)
    {
      Title = "the Tormented";
      Body = 263;
      BaseSoundID = 680;
      Hue = 0x835;

      SetStr(1419, 1438);
      SetDex(309, 413);
      SetInt(129, 131);

      SetHits(4100, 4200);

      SetDamage(16, 30);

      SetDamageType(ResistanceType.Physical, 100);

      SetResistance(ResistanceType.Physical, 65, 90);
      SetResistance(ResistanceType.Fire, 65, 70);
      SetResistance(ResistanceType.Cold, 50, 60);
      SetResistance(ResistanceType.Poison, 40, 60);
      SetResistance(ResistanceType.Energy, 50, 55);

      //SetSkill( SkillName.Meditation, Unknown );
      //SetSkill( SkillName.EvalInt, Unknown );
      //SetSkill( SkillName.Magery, Unknown );
      //SetSkill( SkillName.Poisoning, Unknown );
      SetSkill(SkillName.Anatomy, 0);
      SetSkill(SkillName.MagicResist, 107.0, 111.3);
      SetSkill(SkillName.Tactics, 107.0, 117.0);
      SetSkill(SkillName.Wrestling, 100.0, 105.0);

      Fame = 70000;
      Karma = -70000;

      VirtualArmor = 28; // Don't know what it should be

      if (Core.ML)
      {
        PackResources(8);
        PackTalismans(5);
      }

      Timer.DelayCall(TimeSpan.FromSeconds(1), SpawnTormented);
    }

    public Meraktus(Serial serial) : base(serial)
    {
    }

    public override string CorpseName => "the remains of Meraktus";
    public override ChampionSkullType SkullType => ChampionSkullType.Pain;

    public override Type[] UniqueList => new[] { typeof(Subdue) };
    public override Type[] SharedList => new Type[] { };

    public override Type[] DecorativeList => new[]
    {
      typeof(ArtifactLargeVase),
      typeof(ArtifactVase),
      typeof(MinotaurStatueDeed)
    };

    public override MonsterStatuetteType[] StatueTypes => new[] { MonsterStatuetteType.Minotaur };

    public override string DefaultName => "Meraktus";

    public override int Meat => 2;
    public override int Hides => 10;
    public override HideType HideType => HideType.Regular;
    public override Poison PoisonImmune => Poison.Regular;
    public override int TreasureMapLevel => 3;
    public override bool BardImmune => true;
    public override bool Unprovokable => true;
    public override bool Uncalmable => true;

    public override WeaponAbility GetWeaponAbility() => WeaponAbility.Dismount;

    public virtual void PackResources(int amount)
    {
      for (int i = 0; i < amount; i++)
        switch (Utility.Random(6))
        {
          case 0:
            PackItem(new Blight());
            break;
          case 1:
            PackItem(new Scourge());
            break;
          case 2:
            PackItem(new Taint());
            break;
          case 3:
            PackItem(new Putrefication());
            break;
          case 4:
            PackItem(new Corruption());
            break;
          case 5:
            PackItem(new Muculent());
            break;
        }
    }

    public virtual void PackTalismans(int amount)
    {
      int count = Utility.Random(amount);

      for (int i = 0; i < count; i++)
        PackItem(new RandomTalisman());
    }

    public override void OnDeath(Container c)
    {
      base.OnDeath(c);

      if (!Core.ML)
        return;

      c.DropItem(new MalletAndChisel());

      switch (Utility.Random(3))
      {
        case 0:
          c.DropItem(new MinotaurHedge());
          break;
        case 1:
          c.DropItem(new BonePile());
          break;
        case 2:
          c.DropItem(new LightYarn());
          break;
      }

      if (Utility.RandomBool())
        c.DropItem(new TormentedChains());

      if (Utility.RandomDouble() < 0.025)
        c.DropItem(new CrimsonCincture());
    }

    public override void GenerateLoot()
    {
      if (Core.ML)
        AddLoot(LootPack.AosSuperBoss, 5); // Need to verify
    }

    public override int GetAngerSound() => 0x597;

    public override int GetIdleSound() => 0x596;

    public override int GetAttackSound() => 0x599;

    public override int GetHurtSound() => 0x59a;

    public override int GetDeathSound() => 0x59c;

    public override void OnGaveMeleeAttack(Mobile defender)
    {
      base.OnGaveMeleeAttack(defender);

      if (0.2 >= Utility.RandomDouble())
        Earthquake();
    }

    public void Earthquake()
    {
      IPooledEnumerable<Mobile> eable = GetMobilesInRange(8);

      foreach (Mobile m in eable)
      {
        if (m == this || !CanBeHarmful(m) || m.Deleted || !m.Player &&
            !(m is BaseCreature creature && (creature.Controlled || creature.Summoned || creature.Team != Team)))
          continue;

        if (m is PlayerMobile pm && pm.Mounted)
            pm.Mount.Rider = null;

        int damage = (int)(m.Hits * 0.6);
        if (damage < 10)
          damage = 10;
        else if (damage > 75)
          damage = 75;
        DoHarmful(m);
        AOS.Damage(m, this, damage, 100, 0, 0, 0, 0);
        if (m.Alive && m.Body.IsHuman && !m.Mounted)
          m.Animate(20, 7, 1, true, false, 0); // take hit
      }

      eable.Free();
    }

    public override void Serialize(GenericWriter writer)
    {
      base.Serialize(writer);
      writer.Write(0);
    }

    public override void Deserialize(GenericReader reader)
    {
      base.Deserialize(reader);
      int version = reader.ReadInt();
    }

    #region SpawnHelpers

    public void SpawnTormented()
    {
      BaseCreature spawna = new TormentedMinotaur();
      spawna.MoveToWorld(Location, Map);

      BaseCreature spawnb = new TormentedMinotaur();
      spawnb.MoveToWorld(Location, Map);

      BaseCreature spawnc = new TormentedMinotaur();
      spawnc.MoveToWorld(Location, Map);

      BaseCreature spawnd = new TormentedMinotaur();
      spawnd.MoveToWorld(Location, Map);
    }

    #endregion
  }
}