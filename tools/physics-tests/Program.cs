using System;
using System.IO;
using Microsoft.Xna.Framework;
using TerraCommand.Common.Actors;

// CCActor and ActorMotion are compiled directly from the mod, without loading the game.
sealed class TestTerrain : IActorTerrain
{
    public float Floor = 0, Wall = 10000, WallThickness = float.PositiveInfinity;
    public bool OneWay, Bump;
    public bool Sample(Vector2 p, out ContactSurface s) { s = new ContactSurface(0.8f, 0.05f, OneWay, Floor); return (Bump && p.X >= -112 && p.X < -96 && p.Y >= Floor - 6) || (p.Y >= Floor && (!OneWay || p.Y < Floor + 2)) || p.X >= Wall && p.X < Wall + WallThickness; }
}
static class Program
{
    static int checks;
    static void Check(bool result, string description)
    {
        if (!result) throw new Exception(description);
        checks++;
        Console.WriteLine("PASS " + description);
    }
    static void Near(float actual, float expected, string description, float tolerance = 0.0001f) =>
        Check(Math.Abs(actual - expected) < tolerance, $"{description}: {actual:0.00000} (reference {expected:0.00000})");

    static void Main()
    {
        // Source: Scene global acceleration, 20 m/s², 20 pixels/metre, 60 Hz semi-implicit integration.
        var falling = new CCActor { Grounded = false };
        float distance = 0;
        for (int i = 0; i < 60; i++) { falling.Physics(default); distance += falling.Vel.Y; }
        Near(falling.Vel.Y, 20f / 3f, "one-second freefall velocity");
        Near(distance, 20f * 20f / 3600f * (60 * 61 / 2f), "one-second freefall displacement", 0.001f);

        // Source: CoalitionLight.ini overrides Heavy jetpack to 12000 PPM. Three emissions share the rate.
        // Mean speeds: Heavy blast 18, inherited second blast 17.5, inherited smoke 15; all masses 1.25kg.
        const float force = (12000f / 60f / 3f) * 1.25f * (18 + 17.5f + 15);
        const float burstImpulse = 3 * 2 * 1.25f * (18 + 17.5f + 15); // ceil(8 / 3) particles per emission
        Near(CC.Mass, 133, "resolved torso/head/helmet/limbs/feet/jetpack mass");
        Near(CC.JetForce, force, "resolved soldier jetpack continuous force");
        Near(CC.JetBurstImpulse, burstImpulse, "resolved soldier jetpack burst impulse");
        var jet = new CCActor { Grounded = false };
        var press = new ActorInput { Jump = true, JumpStart = true, Aim = new Vector2(100, 0) };
        jet.Control(press);
        Near(jet.Fuel, 80, "burst consumes nine particles plus one thrust tick");
        Near(jet.Vel.Length(), burstImpulse / 133f / 3f + force * 1.3f * 2 / 133f / 180f,
            "burst scales continuous emissions as well as extra particles");
        Vector2 before = jet.Vel;
        jet.Control(press);
        Near((jet.Vel - before).Length(), force * (0.7f + 0.6f * 80f / 90f) / 133f / 180f,
            "repeated trigger cannot bypass 160ms burst spacing");
        Near(jet.Fuel, 74.5f, "rejected burst uses half burst fuel plus continuous fuel");
        Check(jet.BurstCooldown == 9, "rejected burst does not restart cooldown");
        var exhausted = new CCActor { Fuel = 15 };
        exhausted.Control(new ActorInput { Jump = true });
        Check(!exhausted.Jetting, "cannot restart at exactly 250ms fuel");
        Near(exhausted.Fuel, 16.5f, "fuel recharges at the source rate");
        exhausted.Control(new ActorInput { Jump = true });
        Check(exhausted.Jetting, "held thrust resumes above the minimum fuel threshold");

        // Source: AHuman::Update balance spring uses current health/max health.
        var healthy = new CCActor { Grounded = false, AngVel = 10, Health = 100 };
        var wounded = new CCActor { Grounded = false, AngVel = 10, Health = 10 };
        healthy.Physics(default); wounded.Physics(default);
        Near(healthy.AngVel, 9.2f, "full-health balance damping");
        Near(wounded.AngVel, 9.74f, "wounded balance damping");

        // Source: Coalition SharpAimDelay=250ms; AHuman's 0.035 convergence begins after that delay.
        var aiming = new CCActor();
        var aim = new ActorInput { SharpAim = true, HoldingDevice = true, Aim = new Vector2(100, 0) };
        for (int i = 0; i < 15; i++) aiming.Control(aim);
        Near(aiming.SharpAim, 0, "sharp aim waits for the 250ms settling delay");
        aiming.Control(aim);
        Near(aiming.SharpAim, 0.035f, "first sharp-aim convergence step");
        aim.Reloading = true;
        for (int i = 0; i < 30; i++) aiming.Control(aim);
        Near(aiming.SharpAim, 0, "reloading prevents sharp-aim accumulation");
        aim.Reloading = false;
        for (int i = 0; i < 15; i++) aiming.Control(aim);
        Near(aiming.SharpAim, 0, "reloading restarts the settling delay");

        aim.HoldingDevice = false;
        for (int i = 0; i < 120; i++) aiming.Control(aim);
        Near(aiming.SharpAim, 0, "empty hands cannot build sharp aim");

        // New body state must survive both current wire format and old 36-byte saves.
        jet.ProneTicks = 18;
        var motion = ActorMotion.Capture(jet, jet.Vel);
        var restored = ActorMotion.Load(motion.Save());
        Check(restored.BurstCooldown == 9 && restored.ProneTicks == 18, "body snapshot keeps burst and prone timers");
        var old = motion.Save()[..36];
        var legacy = ActorMotion.Load(old);
        Near(legacy.Fuel, motion.Fuel, "v0.7.0 physical snapshots still load");
        Check(legacy.BurstCooldown == 0, "legacy snapshots default the new cooldown");

        var walking = new CCActor();
        var walk = new ActorInput { Right = true, Aim = new Vector2(100, 0) };
        for (int i = 0; i < 20; i++) { walking.Control(walk); walking.Physics(walk); }
        using var stream = new MemoryStream();
        walking.WriteGait(new BinaryWriter(stream)); stream.Position = 0;
        var remote = new CCActor(); remote.ReadGait(new BinaryReader(stream));
        Check(remote.Move == walking.Move && remote.Legs[0].Path == walking.Legs[0].Path, "remote rig receives the simulated movement path");
        Near(remote.Legs[0].Progress, walking.Legs[0].Progress, "remote front leg phase", 1f / 255f + 0.00001f);
        Near(remote.Legs[1].Progress, walking.Legs[1].Progress, "remote back leg phase", 1f / 255f + 0.00001f);
        Check(stream.Position == stream.Length, "gait packet is consumed exactly");
        float Crawl(bool holding, out CCActor actor)
        {
            actor = new CCActor();
            var input = new ActorInput { Prone = true, HoldingDevice = holding, Aim = new Vector2(100, 0) };
            for (int i = 0; i < 100; i++) { actor.Control(input); actor.Physics(input); actor.AfterMove(new Vector2(actor.Vel.X, 0), true); }
            input.Right = true;
            float travel = 0;
            for (int i = 0; i < 150; i++) { actor.Control(input); actor.Physics(input); travel += actor.Vel.X; actor.AfterMove(new Vector2(actor.Vel.X, 0), true); }
            return travel;
        }
        float armedTravel = Crawl(true, out var armedCrawler);
        float freeTravel = Crawl(false, out var freeCrawler);
        Check(armedCrawler.Hands[0].Path == null && armedCrawler.Hands[1].Path == LimbPaths.ArmCrawl,
            "armed crawling keeps the front hand on its device and uses the rear arm");
        Check(freeCrawler.Hands[0].Path == LimbPaths.ArmCrawl && freeCrawler.Hands[1].Path == LimbPaths.ArmCrawl,
            "empty-handed crawling uses both arms");
        Check(freeTravel > armedTravel && armedTravel > 0, "free arms improve crawl propulsion");
        var crawlMotion = ActorMotion.Capture(freeCrawler, freeCrawler.Vel);
        var resumed = new CCActor();
        ActorMotion.Load(crawlMotion.Save()).Apply(resumed, ActorKind.Soldier);
        Near(resumed.Legs[0].Progress, freeCrawler.Legs[0].Progress, "body handoff keeps foot phase", 1f / 255f + 0.00001f);
        Near(resumed.Hands[1].Progress, freeCrawler.Hands[1].Progress, "body handoff keeps crawl-arm phase", 1f / 255f + 0.00001f);
        var lying = new CCActor { Prone = ProneState.LayingProne, Rot = MathF.PI / 2 };
        Check(lying.WantedWidth() == 42 && lying.WantedHeight() == 20, "prone collision spans the body length");
        lying.Rot = 0;
        Check(lying.WantedWidth() == 14 && lying.WantedHeight() == 42, "upright collision dimensions");
        // Terrain-driven contacts exercise the production solver without Terraria's hitbox collision.
        Near(ActorContacts.EffectiveMass(10, 100, new Vector2(0, 2), Vector2.UnitX), 1f / 0.14f, "off-centre contact effective mass");
        Vector2 impulseVelocity = Vector2.Zero; float impulseOmega = 0;
        ActorContacts.ApplyImpulse(ref impulseVelocity, ref impulseOmega, new Vector2(10, 0), new Vector2(0, 2), 10, 100);
        Near(impulseVelocity.X, 1, "contact linear momentum response");
        Near(impulseOmega, -0.2f, "off-centre contact generates angular momentum");
        var terrain = new TestTerrain();
        Vector2 sweep = ActorContacts.SweepPoint(terrain, new Vector2(0, -20), new Vector2(0, 100), false, out var hitNormal, out _);
        Check(sweep.Y < 0 && sweep.Y > -1 && hitNormal.Y < -0.9f, "fast point travel cannot tunnel through the floor");
        var contactActor = new CCActor { ContactPhysics = true };
        Vector2 contactFeet = new Vector2(0, -1);
        var idle = new ActorInput { Aim = new Vector2(100, 0) };
        for (int i = 0; i < 240; i++) {
            contactActor.Control(idle); contactActor.Physics(idle);
            contactFeet = ActorContacts.Step(contactActor, contactFeet, idle, terrain);
            if (i % 60 == 0) Console.WriteLine($"CONTACT stand {i}: feet={contactFeet} vel={contactActor.Vel} rot={contactActor.Rot} grounded={contactActor.Grounded} stuck={contactActor.Contacts.Stuck}");
        }
        Check(!contactActor.Contacts.Stuck && contactFeet.Y > -8 && contactFeet.Y < 2, "standing contact supports the body without rectangular collision");
        Check(contactActor.Grounded, "support is reported by terrain contact groups");
        float startX = contactFeet.X;
        idle.Right = true;
        for (int i = 0; i < 120; i++) {
            contactActor.Control(idle); contactActor.Physics(idle);
            contactFeet = ActorContacts.Step(contactActor, contactFeet, idle, terrain);
        }
        Console.WriteLine($"CONTACT walk: moved={contactFeet.X-startX} feet={contactFeet} rot={contactActor.Rot} stuck={contactActor.Contacts.Stuck}");
        Check(contactFeet.X - startX > 20, "blocked foot strokes propel the body");
        var wallTerrain = new TestTerrain { Floor = 10000, Wall = 0, WallThickness = 1 };
        var fastBody = new CCActor { ContactPhysics = true, Grounded = false, Vel = new Vector2(80, 0) };
        Vector2 stopped = ActorContacts.Step(fastBody, new Vector2(-40, 0), default, wallTerrain);
        var bodyAtoms = new Vector2[1024];
        int atomCount = ContactGeometry.Build(fastBody, stopped, fastBody.Rot, bodyAtoms);
        bool intersects = false;
        for (int i = 0; i < atomCount; i++) if (wallTerrain.Sample(bodyAtoms[i], out _)) intersects = true;
        Console.WriteLine($"CONTACT wall feet={stopped} velocity={fastBody.Vel} angle={fastBody.Rot} stuck={fastBody.Contacts.Stuck} intersects={intersects}");
        Check(!intersects && (stopped + ContactGeometry.HipOffset(fastBody) + ContactGeometry.Rotate(new Vector2(0, -8), fastBody.Rot)).X < 0, "swept actor atoms cannot tunnel through a one-pixel wall");
        var spinner = new CCActor { ContactPhysics = true, Grounded = false, AngVel = 50 };
        Vector2 rotatedFeet = ActorContacts.Step(spinner, new Vector2(-18, 0), default, wallTerrain);
        atomCount = ContactGeometry.Build(spinner, rotatedFeet, spinner.Rot, bodyAtoms);
        intersects = false;
        for (int i = 0; i < atomCount; i++) if (wallTerrain.Sample(bodyAtoms[i], out _)) intersects = true;
        Check(!intersects, "angular sweep keeps rotating body parts outside a thin wall");
        var platform = new TestTerrain { OneWay = true };
        Vector2 rising = ActorContacts.SweepPoint(platform, new Vector2(0, 5), new Vector2(0, -10), false, out _, out _);
        Near(rising.Y, -5, "one-way platforms permit upward travel");
        Vector2 dropping = ActorContacts.SweepPoint(platform, new Vector2(0, -5), new Vector2(0, 10), true, out _, out _);
        Near(dropping.Y, 5, "drop-through input releases one-way platforms");
        Vector2 landing = ActorContacts.SweepPoint(platform, new Vector2(0, -5), new Vector2(0, 10), false, out _, out _);
        Check(landing.Y < 0 && landing.Y > -1, "one-way platforms support downward travel");
        using var contactStream = new MemoryStream();
        ContactSnapshot.Capture(contactActor.Contacts).Write(new BinaryWriter(contactStream)); contactStream.Position = 0;
        var snapshot = ContactSnapshot.Read(new BinaryReader(contactStream));
        var restoredContacts = new ActorContactState(); snapshot.Apply(restoredContacts);
        Check(restoredContacts.Initialized && restoredContacts.Anchor == contactActor.Contacts.Anchor && restoredContacts.Limbs[0].Position == contactActor.Contacts.Limbs[0].Position,
            "contact anchors and limb positions round-trip in network/save snapshots");
        Check(contactStream.Position == contactStream.Length, "contact snapshot consumes exactly its payload");
        var contactResumed = new CCActor { ContactPhysics = true };
        ActorMotion.Capture(contactActor, contactActor.Vel).Apply(contactResumed, ActorKind.Soldier);
        Vector2 contactResumedFeet = contactFeet;
        contactActor.Control(idle); contactActor.Physics(idle);
        contactFeet = ActorContacts.Step(contactActor, contactFeet, idle, terrain);
        contactResumed.Control(idle); contactResumed.Physics(idle);
        contactResumedFeet = ActorContacts.Step(contactResumed, contactResumedFeet, idle, terrain);
        Check(Vector2.Distance(contactFeet, contactResumedFeet) < 1 && Vector2.Distance(contactActor.Contacts.Limbs[0].Position, contactResumed.Contacts.Limbs[0].Position) < 1,
            "control handoff resumes the physical stride without restarting its contact tips");
        // Moving terrain invalidates old grips; they must be released instead of pinning the actor underground.
        terrain.Floor = -4;
        idle.Right = false;
        for (int i = 0; i < 90; i++) { contactActor.Control(idle); contactActor.Physics(idle); contactFeet = ActorContacts.Step(contactActor, contactFeet, idle, terrain); }
        Check(!contactActor.Contacts.Stuck && contactFeet.Y < 0, "terrain placed around planted feet does not retain buried grips");
        var stroke = new Gait();
        stroke.Begin(LimbPaths.Crab, 0);
        stroke.ReportPosition(LimbPaths.Crab.Start);
        Check(stroke.Segment == 0, "progress cannot skip from the start to collision-disabled return segments");
        stroke.ReportPosition(LimbPaths.Crab.SegmentTarget(0));
        Check(stroke.Segment == 1 && stroke.Dist > 0, "actual tip position advances the current segment");
        var crab = new CCActor { ContactPhysics = true };
        crab.Apply(ActorStats.Get(ActorKind.Crab), 0);
        var crabInput = new ActorInput { Right = true, Aim = new Vector2(100, 0) };
        Vector2 crabFeet = new(0, -5);
        for (int i = 0; i < 180; i++) { crab.Control(crabInput); crab.Physics(crabInput); crabFeet = ActorContacts.Step(crab, crabFeet, crabInput, terrain); }
        Console.WriteLine($"CONTACT crab feet={crabFeet} status={crab.Status} stuck={crab.Contacts.Stuck}");
        Check(crabFeet.X > 10 && !crab.Contacts.Stuck, "four-legged actors use contact-driven walking");
        var crawler = new CCActor { ContactPhysics = true };
        Vector2 crawlFeet = new(0, -5);
        var crawlInput = new ActorInput { Prone = true, Aim = new Vector2(100, 0) };
        for (int i = 0; i < 150; i++) { crawler.Control(crawlInput); crawler.Physics(crawlInput); crawlFeet = ActorContacts.Step(crawler, crawlFeet, crawlInput, terrain); }
        float crawlStart = crawlFeet.X;
        crawlInput.Right = true;
        for (int i = 0; i < 180; i++) { crawler.Control(crawlInput); crawler.Physics(crawlInput); crawlFeet = ActorContacts.Step(crawler, crawlFeet, crawlInput, terrain); }
        Console.WriteLine($"CONTACT crawl moved={crawlFeet.X-crawlStart} angle={crawler.Rot} stuck={crawler.Contacts.Stuck}");
        Check(crawlFeet.X - crawlStart > 10 && !crawler.Contacts.Stuck, "terrain-tested hands and feet drive crawling");
        var bumpTerrain = new TestTerrain { Bump = true };
        var stepper = new CCActor { ContactPhysics = true };
        Vector2 stepFeet = new(0, -1);
        var backwards = new ActorInput { Left = true, Aim = new Vector2(100, 0) };
        for (int i = 0; i < 840; i++) { stepper.Control(backwards); stepper.Physics(backwards); stepFeet = ActorContacts.Step(stepper, stepFeet, backwards, bumpTerrain); }
        Console.WriteLine($"CONTACT bump feet={stepFeet} rot={stepper.Rot} stuck={stepper.Contacts.Stuck}");
        Check(stepFeet.X < -125, "terrain-driven strides climb a six-pixel bump while walking backwards");
        var recovering = new CCActor { ContactPhysics = true, Status = ActorStatus.Unstable, Vel = new Vector2(3, -1) };
        var recoverInput = new ActorInput { Aim = new Vector2(100, 0) };
        Vector2 recoverFeet = new(0, -1);
        for (int i = 0; i < 300; i++) { recovering.Control(recoverInput); recovering.Physics(recoverInput); recoverFeet = ActorContacts.Step(recovering, recoverFeet, recoverInput, new TestTerrain()); }
        Console.WriteLine($"CONTACT recover: status={recovering.Status} v={recovering.Vel} angle={recovering.Rot} omega={recovering.AngVel}");
        Check(recovering.Status == ActorStatus.Stable, "a modest shove recovers on a flat surface");
        var fallBody = new CCActor { ContactPhysics = true };
        Vector2 fallFeet = new(0, -400);
        float fallDamage = 0;
        for (int i = 0; i < 150; i++) {
            fallBody.Control(default); fallBody.Physics(default);
            fallFeet = ActorContacts.Step(fallBody, fallFeet, default, new TestTerrain());
            fallDamage += fallBody.ImpactDamage(fallBody.Contacts.TravelImpulse.Length() / fallBody.Mass);
        }
        Check(fallDamage > 0 && fallDamage < 1, "an unladen upright body takes survivable contact damage in the integrated 400-pixel fall fixture");
        Console.WriteLine($"{checks} reference physics checks passed");
    }
}
