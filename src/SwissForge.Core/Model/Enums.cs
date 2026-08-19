namespace SwissForge.Core.Model
{
    /// <summary>Broad machinability family. Drives the base cutting-speed lookup.</summary>
    public enum MaterialGroup
    {
        FreeMachiningSteel,   // 12L14, 1215
        CarbonSteel,          // 1018, 1045
        AlloySteel,           // 4140, 8620
        StainlessAustenitic,  // 303, 304, 316
        StainlessMartensitic, // 416, 17-4PH
        Aluminum,             // 6061, 7075, 2011
        Brass,                // 360, C36000
        Copper,
        Titanium,             // Ti-6Al-4V
        Nickel,               // Inconel, Monel
        Plastic,              // Delrin, PEEK, PTFE
        Other
    }

    /// <summary>Cutting-tool substrate. Multiplies the base cutting speed.</summary>
    public enum ToolMaterial { HSS, Cobalt, Carbide, CoatedCarbide, Cermet, CBN, PCD }

    /// <summary>What the tool does. Drives which feed/speed model applies.</summary>
    public enum OperationType
    {
        Face,
        TurnRough,
        TurnFinish,
        Groove,
        Thread,
        Drill,
        Bore,
        Ream,
        Tap,
        Mill,
        Knurl,
        Broach,
        Polygon,
        CrossDrill,
        Cutoff,
        BackFace,
        BackDrill,
        BackTap,
        BackTurn,
        Transfer,   // handoff of the bar from main to sub spindle
        BarFeed,
        Custom
    }

    /// <summary>Which spindle holds the work.</summary>
    public enum SpindleSide { Main, Sub }

    /// <summary>
    /// Where the tool lives on a Swiss machine. Position matters for collision
    /// checking and for whether an operation can overlap with another channel.
    /// </summary>
    public enum ToolStation
    {
        GangSlide,      // main tool post / gang plate, OD tools
        TurretStation,  // turret-style Swiss
        LiveToolFront,  // rotary tools working the main spindle
        LiveToolBack,   // rotary tools working the sub spindle
        BackWorking,    // fixed back-working tools on the sub spindle
        EndworkingSlide,// drilling sleeve / endworking attachment
        Auxiliary
    }

    /// <summary>Control family. Decides sync-code syntax and program layout.</summary>
    public enum ControlDialect
    {
        FanucGeneric,
        CitizenCincom,   // Mitsubishi Meldas based, uses $1/$2/$3 and !L codes
        StarSR,          // Fanuc based, uses M-code waits (M100-M199) or !
        TsugamiFanuc,
        HanwhaXD,
        Generic
    }

    /// <summary>Severity for linter and validation findings.</summary>
    public enum Severity { Info, Warning, Error, Critical }
}
