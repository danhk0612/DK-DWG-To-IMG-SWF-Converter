namespace DwgToPngPoC;

internal enum DrawingRiskLevel
{
    Normal,
    Caution,
    High
}

internal enum DrawingRiskDecision
{
    ContinueSelectedOutputs,
    TrySwfOnly,
    SkipFile
}

internal readonly record struct DrawingRiskAssessment(
    DrawingRiskLevel Level,
    long FileSizeBytes,
    int ModelEntityCount,
    int HatchCount,
    int InsertCount,
    int SplineCount,
    bool RequestedSvg,
    bool RequestedPng,
    bool RequestedSwf)
{
    public double FileSizeMb => FileSizeBytes / 1024d / 1024d;

    public static DrawingRiskAssessment Analyze(
        FileInfo inputInfo,
        ACadSharp.CadDocument document,
        ConverterSettings settings)
    {
        var entityCount = document.ModelSpace.Entities.Count;
        var hatchCount = 0;
        var insertCount = 0;
        var splineCount = 0;

        foreach (var entity in document.ModelSpace.Entities)
        {
            switch (entity.GetType().Name)
            {
                case "Hatch":
                    hatchCount++;
                    break;
                case "Insert":
                    insertCount++;
                    break;
                case "Spline":
                    splineCount++;
                    break;
            }
        }

        // These are intentionally conservative heuristics based on the observed failure range
        // of the current rendering paths, not a claim about DWG complexity in general.
        var level = inputInfo.Length >= 100L * 1024L * 1024L || entityCount >= 500_000
            ? DrawingRiskLevel.High
            : inputInfo.Length >= 50L * 1024L * 1024L || entityCount >= 300_000
                ? DrawingRiskLevel.Caution
                : DrawingRiskLevel.Normal;

        return new DrawingRiskAssessment(
            level,
            inputInfo.Length,
            entityCount,
            hatchCount,
            insertCount,
            splineCount,
            settings.ExportSvg,
            settings.ExportPng,
            settings.ExportSwf);
    }

    public string ToLogText()
    {
        var levelText = Level switch
        {
            DrawingRiskLevel.High => "고위험",
            DrawingRiskLevel.Caution => "주의",
            _ => "일반"
        };

        return $"위험도 {levelText} / {FileSizeMb:0.0} MB / 모델 엔티티 {ModelEntityCount:N0}개 / " +
               $"Hatch {HatchCount:N0} / Insert {InsertCount:N0} / Spline {SplineCount:N0}";
    }
}
