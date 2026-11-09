using System;
using System.IO;
using Microsoft.Xna.Framework;
using TerraCommand.Common.Actors;
using TerraCommand.Common.Physics;
using TerraCommand.Content.NPCs;

// CCActor and ActorMotion are compiled directly from the mod, without loading the game.
sealed class TestTerrain : IActorTerrain
{
    public float Floor = 0, Wall = 10000, WallThickness = float.PositiveInfinity;
    public bool OneWay, Bump;
    public bool Sample(Vector2 p, out ContactSurface s) { s = new ContactSurface(0.8f, 0.05f, OneWay, Floor); return (Bump && p.X >= -112 && p.X < -96 && p.Y >= Floor - 6) || (p.Y >= Floor && (!OneWay || p.Y < Floor + 2)) || p.X >= Wall && p.X < Wall + WallThickness; }
}
sealed class YieldTerrain : IPenetrableTerrain {
    readonly System.Collections.Generic.HashSet<(int,int)> holes=new();
    public int Removed=>holes.Count;
    public bool Sample(Vector2 p,out ContactSurface surface) { surface=new(.85f,.1f); return p.Y>=0 && p.Y<2 && !holes.Contains(((int)MathF.Floor(p.X/2),(int)MathF.Floor(p.Y/2))); }
    public bool Penetrate(Vector2 p,float momentum,out float loss) {
        if(!Sample(p,out _) || !MaterialSolver.Penetration(momentum,70,4,out loss)) { loss=0; return false; }
        holes.Add(((int)MathF.Floor(p.X/2),(int)MathF.Floor(p.Y/2))); return true;
    }
}
sealed class BulletSlab : IBulletMatter {
    public float Work=560;
    public bool Protected;
    public int Spends;
    public bool Sample(Vector2 p,out float resistance) { resistance=Protected?float.PositiveInfinity:Work;return p.X>=2 && p.X<4 && Work>0; }
    public void Spend(Vector2 p,float work) { Work=Math.Max(0,Work-work);Spends++; }
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
        ActorMotion.Load(crawlMotion.Save()).Apply(resumed, ActorKind.Light);
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
        int supportedTicks = 0;
        for (int i = 0; i < 240; i++) {
            contactActor.Control(idle); contactActor.Physics(idle);
            contactFeet = ActorContacts.Step(contactActor, contactFeet, idle, terrain);
            if (contactActor.Grounded) supportedTicks++;
            if (i % 60 == 0) Console.WriteLine($"CONTACT stand {i}: feet={contactFeet} vel={contactActor.Vel} rot={contactActor.Rot} grounded={contactActor.Grounded} stuck={contactActor.Contacts.Stuck}");
        }
        Check(!contactActor.Contacts.Stuck && contactFeet.Y > -8 && contactFeet.Y < 2, "standing contact supports the body without rectangular collision");
        Check(supportedTicks > 60, "support is repeatedly reported by terrain contact groups");
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
        ActorMotion.Capture(contactActor, contactActor.Vel).Apply(contactResumed, ActorKind.Light);
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
        // Native craft profiles and trajectories, independent of Terraria's entity collision.
        Near(CraftProfile.Get(ShipStyle.Rocket).EmptyMass,948,"rocket resolved mass");
        Near(CraftProfile.Get(ShipStyle.Rocket).MainForce,40500,"rocket resolved continuous force");
        Near(CraftProfile.Get(ShipStyle.Rocket).MainBurst,4320,"rocket resolved burst momentum");
        Near(CraftProfile.Get(ShipStyle.Dropship).EmptyMass,3422,"dropship resolved mass");
        Near(CraftProfile.Get(ShipStyle.Dropship).MainForce,43750,"dropship per-engine force",0.01f);
        Near(ShipPhysics.ThrottleFactor(-1),0.1f,"dropship minimum throttle");
        Near(ShipPhysics.ThrottleFactor(0),0.7f,"dropship neutral throttle");
        Near(ShipPhysics.ThrottleFactor(1),1.3f,"dropship maximum throttle");
        var sky = new TestTerrain { Floor = 100000 };
        var crate = new ShipPhysics(ShipStyle.Crate);
        for (int i=0;i<60;i++) crate.Step(default,0,sky);
        Near(crate.Motion.Velocity.Y,20f/3,"crate uses the actor gravity",0.001f);
        Near(crate.Motion.Position.Y,20*20/3600f*(60*61/2f),"crate freefall distance",0.001f);
        var rocket = new ShipPhysics(ShipStyle.Rocket);
        var loadedRocket = new ShipPhysics(ShipStyle.Rocket);
        rocket.Step(new(0,0,true,false),0,sky);
        loadedRocket.Step(new(0,0,true,false),600,sky);
        Near(rocket.Motion.Velocity.Y,CC.Gravity-(4320f/948/3+40500f*1.5f/948/180),"rocket activation burst and thrust");
        Check(loadedRocket.Motion.Velocity.Y > rocket.Motion.Velocity.Y,"cargo reduces rocket acceleration");
        float vx=rocket.Motion.Velocity.X;
        rocket.Motion.Velocity.X=3;
        rocket.Step(default,0,sky);
        Near(rocket.Motion.Velocity.X,3,"rocket coasts without artificial air drag");
        var tiltedRocket = new ShipPhysics(ShipStyle.Rocket);
        tiltedRocket.Motion.Rotation=0.2f;
        tiltedRocket.Step(new(0,0,true,false),0,sky);
        Check(tiltedRocket.Motion.AngularVelocity<0,"rocket gimbal applies restoring torque after CC angle conversion");
        var steering = new ShipPhysics(ShipStyle.Rocket);
        steering.Step(new(1,0,false,false),0,sky);
        Check(steering.Motion.Velocity.X>0 && steering.Motion.AngularVelocity>0,"rocket side jets apply translation and torque");
        var drop = new ShipPhysics(ShipStyle.Dropship);
        drop.Step(new(1,0,false,false),0,sky);
        Near(drop.LateralControl,-0.1f,"dropship engine slew rate");
        Check(drop.Motion.Velocity.X>0,"dropship steers through engine thrust");
        drop.Step(default,0,sky);
        Near(drop.LateralControl,-0.09f,"dropship engine return rate");
        for(int i=0;i<600;i++) drop.Step(default,0,sky);
        Check(Math.Abs(drop.Motion.Velocity.Y)<0.5f,"dropship neutral vertical controller settles near hover");
        var loadedDrop = new ShipPhysics(ShipStyle.Dropship);
        for(int i=0;i<600;i++) loadedDrop.Step(default,800,sky);
        Check(Math.Abs(loadedDrop.Motion.Velocity.Y)<0.5f,"dropship compensates for a full cargo load while hovering");
        float beforeMomentum=loadedDrop.Motion.Mass*loadedDrop.Motion.Velocity.X;
        loadedDrop.Motion.Impulse(new Vector2(900,0),loadedDrop.Centre+new Vector2(0,20));
        Near(loadedDrop.Motion.Mass*loadedDrop.Motion.Velocity.X-beforeMomentum,900,"craft external impulse conserves linear momentum",0.001f);
        Check(loadedDrop.Motion.AngularVelocity<0,"off-centre craft impulse produces angular momentum");
        var hullWall = new TestTerrain { Floor=100000,Wall=0,WallThickness=1 };
        var fastCraft=new ShipPhysics(ShipStyle.Dropship);
        fastCraft.Motion.Position=new(-130,0); fastCraft.Motion.Velocity=new(100,0);
        fastCraft.Step(default,0,hullWall);
        var craftAtoms=new Vector2[4096];
        int craftCount=fastCraft.Build(fastCraft.Motion.Position,fastCraft.Motion.Rotation,craftAtoms);
        bool craftIntersects=false;
        for(int i=0;i<craftCount;i++) if(hullWall.Sample(craftAtoms[i],out _)) craftIntersects=true;
        Check(!craftIntersects && fastCraft.Centre.X<0,"fast rotating craft cannot tunnel through a one-pixel wall");
        var craftLanding=new ShipPhysics(ShipStyle.Rocket);
        craftLanding.Motion.Position=new(0,-40);
        for(int i=0;i<150;i++) craftLanding.Step(default,0,new TestTerrain());
        Check(!craftLanding.Motion.Stuck && Math.Abs(craftLanding.Motion.Position.Y)<3,"rocket lands on physical gear without snapping");
        using(var craftData=new MemoryStream()) {
            using(var w=new BinaryWriter(craftData,System.Text.Encoding.UTF8,true)) drop.Write(w);
            craftData.Position=0; var copy=new ShipPhysics(ShipStyle.Dropship);
            using(var r=new BinaryReader(craftData,System.Text.Encoding.UTF8,true)) copy.Read(r);
            Near(copy.Motion.AngularVelocity,drop.Motion.AngularVelocity,"craft angular velocity snapshot");
            Near(copy.LeftThrottle,drop.LeftThrottle,"craft engine snapshot");
            Check(craftData.Position==craftData.Length,"craft snapshot consumes exactly its payload");
        }
        var footProbe=new CCActor();
        var toeWall=new TestTerrain { Floor=100000,Wall=2,WallThickness=1 };
        ActorContacts.SweepLimb(footProbe,0,toeWall,new(-2,0),new(2,0),false,out var toeHit,out _);
        Check(toeHit!=Vector2.Zero,"toe atoms contact a rim before the sole centre");
        ActorContacts.SweepLimb(footProbe,2,new TestTerrain {Floor=100000,Wall=-4,WallThickness=1},new(1,0),new(-1,0),false,out var gloveHit,out _);
        Check(gloveHit!=Vector2.Zero,"glove atoms contact a wall before the wrist");
        Near(PhysicalMaterial.Earth.Integrity,70,"source earth integrity");
        Near(PhysicalMaterial.Stone.Integrity,140,"source stone integrity");
        Check(!MaterialSolver.Penetration(279,70,4,out _),"2px earth cell resists subthreshold momentum");
        Check(MaterialSolver.Penetration(560,70,4,out float loss),"earth yields above integrity times cell area");
        Near(loss,0.5f,"penetration removes integrity divided by incoming momentum");
        Check(!MaterialSolver.Penetration(559,140,4,out _),"stone resists a blow that penetrates earth");
        Check(!MaterialSolver.Penetration(float.NaN,70,4,out _),"invalid penetration momentum cannot mutate terrain");
        Check(!fastCraft.Attachments.Has(CraftPart.RightEngine),"terrain contact impulses can tear off the impacted engine");
        var woundedParts=new CraftAttachments();
        bool survivedWounds=true;
        for(int i=0;i<34;i++) survivedWounds &= !woundedParts.Wound(CraftPart.LeftEngine);
        Check(survivedWounds,"engine remains attached below its wound limit");
        Check(woundedParts.Wound(CraftPart.LeftEngine),"engine reaches the source 35-wound limit");
        for(int i=0;i<8;i++) woundedParts.Wound(CraftPart.LeftGear);
        Check(woundedParts.Wound(CraftPart.LeftGear),"gear reaches the source nine-wound limit");
        var parts=new CraftAttachments();
        parts.Accumulate(CraftPart.LeftEngine,new Vector2(1400,0));
        Check(parts.Overloaded(CraftPart.LeftEngine),"engine joint fails above source 4000 Ns threshold");
        parts.Begin(); parts.Accumulate(CraftPart.LeftEngine,new Vector2(1000,0));
        Check(!parts.Overloaded(CraftPart.LeftEngine),"joint load resets each travel frame");
        var damagedDrop=new ShipPhysics(ShipStyle.Dropship);
        damagedDrop.Motion.Velocity=new(2,-1); damagedDrop.Motion.AngularVelocity=1;
        Vector2 momentumBefore=damagedDrop.Motion.Velocity*damagedDrop.Motion.Mass;
        Check(damagedDrop.Detach(CraftPart.LeftEngine),"dropship engine detaches once");
        Check(!damagedDrop.Detach(CraftPart.LeftEngine),"detachment cannot duplicate wrecks");
        Near(damagedDrop.EmptyMass,3122,"missing engine removes its 300kg mass");
        var detached=damagedDrop.Detached.Dequeue();
        Vector2 momentumAfter=damagedDrop.Motion.Velocity*damagedDrop.Motion.Mass+detached.Velocity*300;
        Check(Vector2.Distance(momentumBefore,momentumAfter)<0.01f,"part separation conserves linear momentum including spin velocity");
        Check(detached.Velocity!=new Vector2(2,-1),"detached engine inherits rotational point velocity");
        var noEngines=new ShipPhysics(ShipStyle.Dropship);
        noEngines.Detach(CraftPart.LeftEngine); noEngines.Detach(CraftPart.RightEngine);
        noEngines.Step(new(0,1,true,false),0,sky);
        Near(noEngines.Motion.Velocity.Y,CC.Gravity,"missing engines produce no thrust");
        Check(!noEngines.Contains(noEngines.World(new Vector2(-65,0))),"detached engine disappears from the craft silhouette");
        var gearRocket=new ShipPhysics(ShipStyle.Rocket);
        gearRocket.Detach(CraftPart.LeftGear);
        Near(gearRocket.EmptyMass,878,"missing rocket gear removes its 70kg mass");
        using(var data=new MemoryStream()) {
            damagedDrop.Attachments.Wound(CraftPart.RightEngine);
            using(var w=new BinaryWriter(data,System.Text.Encoding.UTF8,true)) damagedDrop.Write(w);
            data.Position=0; var copy=new ShipPhysics(ShipStyle.Dropship);
            using(var r=new BinaryReader(data,System.Text.Encoding.UTF8,true)) copy.Read(r);
            Check(!copy.Attachments.Has(CraftPart.LeftEngine) && copy.Attachments.Has(CraftPart.RightEngine),"detached part mask survives synchronization");
            Check(copy.Attachments.Wounds[1]==1 && data.Position==data.Length,"part wounds and snapshot payload round-trip");
        }
        var recoveringRocket=new ShipPhysics(ShipStyle.Rocket);
        recoveringRocket.Motion.Rotation=.35f;
        float maximumTilt=.35f;
        for(int i=0;i<180;i++) {
            float steer=recoveringRocket.AutomaticRocketSteer(0);
            recoveringRocket.Step(new(steer,0,false,false),0,sky);
            maximumTilt=Math.Max(maximumTilt,Math.Abs(recoveringRocket.Motion.Rotation));
        }
        Check(maximumTilt<.7f,"burst-aware rocket steering bounds a tilted descent");
        Check(recoveringRocket.Motion.Velocity.Y>5,"AI steering cannot sustain lift by alternating side bursts");
        var softGround=new YieldTerrain();
        var fallingEngine=new CraftWreckPhysics(CraftPart.LeftEngine);
        fallingEngine.Motion.Position=new(0,-40); fallingEngine.Motion.Velocity=new(0,20);
        fallingEngine.Step(softGround);
        Check(softGround.Removed>0,"a fast detached engine excavates yielding terrain");
        Check(fallingEngine.Motion.Velocity.Y<20,"excavation consumes momentum rather than carving for free");
        var slowEngine=new CraftWreckPhysics(CraftPart.LeftEngine);
        slowEngine.Motion.Position=new(0,-27); slowEngine.Motion.Velocity=new(0,.2f);
        var firmGround=new YieldTerrain(); slowEngine.Step(firmGround);
        Check(firmGround.Removed==0,"ordinary resting contact does not erode the ground");
        var approachingDrop=new ShipPhysics(ShipStyle.Dropship);
        approachingDrop.Motion.Position=new(-700,-900);
        var landingTerrain=new TestTerrain();
        for(int i=0;i<900;i++) {
            float wantedVx=Math.Clamp(-approachingDrop.Motion.Position.X*.025f,-3,3);
            float steer=Math.Clamp((wantedVx-approachingDrop.Motion.Velocity.X)*.7f,-1,1);
            approachingDrop.Step(new(steer,approachingDrop.AutomaticDropshipVertical(-approachingDrop.Motion.Position.Y-8),true,false),0,landingTerrain);
        }
        Check(approachingDrop.Attachments.Has(CraftPart.LeftHatch) && approachingDrop.Attachments.Has(CraftPart.RightHatch),"delivery approach preserves fragile hatch joints");
        Check(Math.Abs(approachingDrop.Motion.Position.Y+8)<7,"dropship settles within the unloading hover window");
        var supportLimbs=new LimbState();
        Check(supportLimbs.SupportsWeapon(ActorKind.Soldier,false),"intact arms support a two-handed weapon");
        supportLimbs.Remove(2);
        Check(!supportLimbs.SupportsWeapon(ActorKind.Soldier,false) && supportLimbs.SupportsWeapon(ActorKind.Soldier,true),"one remaining arm supports a pistol but not a rifle");
        supportLimbs.Remove(3);
        Check(!supportLimbs.SupportsWeapon(ActorKind.Soldier,true),"no arms cannot support a handheld weapon");
        Check(supportLimbs.SupportsWeapon(ActorKind.Crab,false),"limb loss does not remove a mounted weapon's support");
        var anatomy=new CCActor();
        anatomy.Limbs.Remove(0); anatomy.Limbs.Wound(2);
        anatomy.Apply(ActorStats.Get(ActorKind.Soldier),0);
        Near(anatomy.Mass,CC.Mass-16,"missing leg removes limb and boot mass");
        Check(!anatomy.Limbs.Remove(0),"limbs cannot detach twice");
        var anatomySave=ActorMotion.Capture(anatomy,Vector2.One).Save();
        var restoredBody=new CCActor(); ActorMotion.Load(anatomySave).Apply(restoredBody,ActorKind.Soldier);
        Check(!restoredBody.Limbs.Has(0) && restoredBody.Limbs.Wounds[2]==1,"body handoff and save retain anatomy and wounds");
        Check(restoredBody.BodyId==anatomy.BodyId,"body identity follows saved anatomy");
        var legacyBody=ActorMotion.Load(anatomySave[..^13]);
        Check(legacyBody.Limbs.Missing==0 && legacyBody.BodyId==0,"older body saves load intact without reading past payload");
        anatomy.Limbs.Remove(1); anatomy.Limbs.Remove(2); anatomy.Limbs.Remove(3);
        anatomy.Control(new ActorInput {Aim=Vector2.UnitX});
        Check(anatomy.FrontArmFree && anatomy.Prone!=ProneState.NotProne,"loss of legs forces crawling and loss of arms releases weapon stance");
        var hatchDrop=new ShipPhysics(ShipStyle.Dropship);
        Check(hatchDrop.Detach(CraftPart.LeftHatch),"native dropship hatch detaches");
        Near(hatchDrop.EmptyMass,3222,"detached hatch removes source 200kg mass");
        Check(!hatchDrop.Detach(CraftPart.LeftHatch),"hatch cannot duplicate its physical wreck");
        hatchDrop.BayOpen=true;
        for(int i=0;i<15;i++)hatchDrop.Step(default,0,sky);
        Near(hatchDrop.HatchOpen,1,"hatch opens over source 250ms interval");
        Near(hatchDrop.PartAngle(CraftPart.RightHatch)-hatchDrop.Motion.Rotation,-MathHelper.ToRadians(28),"right hatch reaches source opening angle");
        foreach(var offset in new[]{new Vector2(600,0),new Vector2(-400,-160),new Vector2(350,200)}) {
            Vector2 launch=BulletTravel.AimVelocity(offset,24,2);
            float t=offset.X/launch.X;
            Vector2 impact=launch*t+new Vector2(0,CC.Gravity*(t*t+t/2)*.5f);
            Check(Vector2.Distance(impact,offset)<.01f,"ballistic aim reaches stationary target with substep gravity");
            Near(launch.Length(),24,"ballistic aim preserves muzzle speed",.001f);
        }
        var slab=new BulletSlab();
        var round=BulletTravel.Step(Vector2.Zero,new(100,0),RoundProfile.Get(0),1,slab);
        Check(!round.Stopped && slab.Work==0,"fast bullet penetrates a thin barrier without tunnelling");
        Near(round.Velocity.X,100-560/9.9f,"penetration spends momentum according to source sharpness",.001f);
        var weakSlab=new BulletSlab {Work=280};
        for(int i=0;i<2;i++)Check(BulletTravel.Step(Vector2.Zero,new(12,0),RoundProfile.Get(0),1,weakSlab).Stopped,"weak bullet stops while weakening material");
        Check(!BulletTravel.Step(Vector2.Zero,new(12,0),RoundProfile.Get(0),1,weakSlab).Stopped,"repeated impacts eventually break the cell");
        var protectedSlab=new BulletSlab {Protected=true};
        Check(BulletTravel.Step(Vector2.Zero,new(1000,0),RoundProfile.Get(0),1,protectedSlab).Stopped && protectedSlab.Spends==0,"protected terrain stops bullets without taking work");
        Console.WriteLine($"{checks} reference physics checks passed");
    }
}
