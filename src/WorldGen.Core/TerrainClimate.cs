using System.Diagnostics;

namespace WorldGen;

/// <summary>
/// Parameters for Stage 3 atmospheric temperature, wind circulation, orographic precipitation, and runoff.
/// </summary>
public sealed record ClimateSettings
{
    /// <summary>Mean sea-level temperature at the equator, in degrees Celsius (CL-1).</summary>
    public float EquatorialTempCelsius { get; init; } = 28.0f;

    /// <summary>Mean sea-level temperature at the poles, in degrees Celsius (CL-1).</summary>
    public float PolarTempCelsius { get; init; } = -22.0f;

    /// <summary>Environmental lapse rate in degrees Celsius per kilometer of elevation above sea level (CL-1).</summary>
    public float LapseRateCPerKm { get; init; } = 6.5f;

    /// <summary>Global precipitation multiplier (1.0 = Earth-like ~950 mm/yr land mean) (CL-2).</summary>
    public float PrecipitationScale { get; init; } = 1.0f;

    /// <summary>Strength of windward mountain orographic condensation (CL-3).</summary>
    public float OrographicLiftStrength { get; init; } = 1.15f;

    /// <summary>Strength of leeward rain-shadow drying behind mountain barriers (CL-3).</summary>
    public float RainShadowStrength { get; init; } = 1.25f;

    /// <summary>Typical e-folding inland moisture decay distance in kilometers (CL-2).</summary>
    public float MoistureDecayKm { get; init; } = 1_850f;
}

/// <summary>
/// Output of Stage 3 climate &amp; rainfall simulation (CL-1 through CL-5).
/// </summary>
public sealed class ClimateResult
{
    public required Heightmap Source { get; init; }

    /// <summary>Mean annual surface temperature in degrees Celsius per cell (CL-1).</summary>
    public required float[] TemperatureCelsius { get; init; }

    /// <summary>Mean annual precipitation in millimeters per year per cell (CL-2, CL-3).</summary>
    public required float[] PrecipitationMmPerYear { get; init; }

    /// <summary>Potential evapotranspiration in millimeters per year per cell (CL-4).</summary>
    public required float[] PotentialEvaporationMmPerYear { get; init; }

    /// <summary>Effective surface water runoff (P - E) in millimeters per year per cell fed to Stage 4 Hydrology (CL-4).</summary>
    public required float[] RunoffMmPerYear { get; init; }

    public required ClimateSettings Settings { get; init; }

    public double MeanLandTemperatureCelsius { get; init; }

    public double MeanLandPrecipitationMm { get; init; }

    public double MaxLandPrecipitationMm { get; init; }

    public double MeanLandRunoffMm { get; init; }

    public double AridLandFraction { get; init; }

    public double HumidLandFraction { get; init; }

    public double ElapsedSeconds { get; init; }
}

/// <summary>
/// Stage 3 climate and orographic rainfall model on the sphere.
/// Computes surface temperature (latitude + lapse rate), 3-cell zonal/meridional wind transport
/// (trade winds, westerlies, polar easterlies), upwind ocean moisture advection, windward orographic
/// precipitation lift, leeward rain shadows, and Budyko surface runoff for Stage 4 hydrology.
/// </summary>
public static class TerrainClimate
{
    public static ClimateResult Analyze(
        Heightmap source,
        ClimateSettings settings,
        float planetRadiusMeters = 6_371_000f,
        ParallelProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);

        var sw = Stopwatch.StartNew();

        int width = source.Width;
        int height = source.Height;
        int totalCells = width * height;
        bool wrapsLongitude = source.View.LongitudeSpan >= 359.999;

        ReadOnlySpan<float> elev = source.Elevations;
        var tempC = new float[totalCells];
        var precipMm = new float[totalCells];
        var petMm = new float[totalCells];
        var runoffMm = new float[totalCells];

        double dLatRad = MathUtils.ToRadians(source.View.LatitudeSpan / height);
        double dLonRad = MathUtils.ToRadians(source.View.LongitudeSpan / width);
        float dyKm = (float)Math.Max(0.1, planetRadiusMeters * dLatRad * 1e-3);

        var dxKm = new float[height];
        var cosWeight = new float[height];
        var latDegArr = new double[height];

        for (int y = 0; y < height; y++)
        {
            double latDeg = source.LatitudeAt(y);
            latDegArr[y] = latDeg;
            double latRad = MathUtils.ToRadians(latDeg);
            double cosLat = Math.Max(0.05, Math.Cos(latRad));
            cosWeight[y] = (float)cosLat;
            dxKm[y] = (float)Math.Max(0.2, planetRadiusMeters * cosLat * dLonRad * 1e-3);
        }

        // 1. Compute temperature and potential evapotranspiration (CL-1, CL-4).
        float tEq = settings.EquatorialTempCelsius;
        float tPole = settings.PolarTempCelsius;
        float tSpan = tEq - tPole;
        float lapsePerMeter = settings.LapseRateCPerKm * 1e-3f;

        for (int y = 0; y < height; y++)
        {
            double latRad = MathUtils.ToRadians(latDegArr[y]);
            float sinLat = (float)Math.Sin(latRad);
            float cos2Lat = (float)Math.Cos(2.0 * latRad);

            // Smooth latitudinal sea-level temperature profile with slight subtropical warmth peak.
            float seaLevelTemp = tEq - tSpan * (sinLat * sinLat) + 2.2f * cos2Lat;
            int rowOffset = y * width;

            for (int x = 0; x < width; x++)
            {
                int i = rowOffset + x;
                float h = MathF.Max(0f, elev[i]);
                float t = seaLevelTemp - lapsePerMeter * h;
                tempC[i] = t;

                // Thornthwaite/Turc-inspired potential evapotranspiration (mm/yr), peaking in warm dry subtropics.
                float absLat = (float)Math.Abs(latDegArr[y]);
                float subtropicalDryBoost = 1.0f + 0.45f * MathF.Exp(-MathF.Pow((absLat - 25f) / 11f, 2f));
                float pet = MathF.Max(80f, (320f + 46f * MathF.Max(-5f, t)) * subtropicalDryBoost);
                petMm[i] = pet;
            }
        }

        // 2. Advect atmospheric moisture along prevailing zonal wind belts with Rossby-wave undulation (CL-2, CL-3).
        float decayScaleKm = MathF.Max(200f, settings.MoistureDecayKm);
        float orographicGain = settings.OrographicLiftStrength;
        float shadowStrength = settings.RainShadowStrength;
        float precipScale = settings.PrecipitationScale;

        ParallelProgress.ForRange(height, progress, y =>
        {
            double lat = latDegArr[y];
            float absLat = (float)Math.Abs(lat);
            float stepKm = dxKm[y];

            float westerlyWeight = MathUtils.SmoothStep(24f, 36f, absLat) * (1f - MathUtils.SmoothStep(55f, 67f, absLat));

            int rowOffset = y * width;
            Span<float> pWestToEast = stackalloc float[width];
            Span<float> pEastToWest = stackalloc float[width];

            SweepZonalMoisture(
                source.Elevations.AsSpan(rowOffset, width),
                tempC.AsSpan(rowOffset, width),
                pWestToEast,
                source,
                y,
                width,
                wrapsLongitude,
                leftToRight: true,
                stepKm,
                decayScaleKm,
                orographicGain,
                shadowStrength);

            SweepZonalMoisture(
                source.Elevations.AsSpan(rowOffset, width),
                tempC.AsSpan(rowOffset, width),
                pEastToWest,
                source,
                y,
                width,
                wrapsLongitude,
                leftToRight: false,
                stepKm,
                decayScaleKm,
                orographicGain,
                shadowStrength);

            float wW = 0.22f + 0.56f * westerlyWeight;
            float wE = 1.0f - wW;

            for (int x = 0; x < width; x++)
            {
                precipMm[rowOffset + x] = (wW * pWestToEast[x] + wE * pEastToWest[x]) * precipScale;
            }
        });

        // 3. Apply separable meridional + zonal synoptic diffusion so rain shadows and wind belts form smooth 2D plumes
        //    rather than single-row horizontal streaks.
        var tempBuffer = new float[totalCells];
        int smoothPasses = Math.Clamp(height / 85, 6, 14);
        for (int pass = 0; pass < smoothPasses; pass++)
        {
            Parallel.For(0, height, y =>
            {
                int up1 = Math.Max(0, y - 1);
                int up2 = Math.Max(0, y - 2);
                int down1 = Math.Min(height - 1, y + 1);
                int down2 = Math.Min(height - 1, y + 2);
                int rowOffset = y * width;

                for (int x = 0; x < width; x++)
                {
                    int left = x > 0 ? x - 1 : (wrapsLongitude ? width - 1 : 0);
                    int right = x + 1 < width ? x + 1 : (wrapsLongitude ? 0 : width - 1);

                    float c = precipMm[rowOffset + x];
                    float n1 = precipMm[up1 * width + x];
                    float s1 = precipMm[down1 * width + x];
                    float n2 = precipMm[up2 * width + x];
                    float s2 = precipMm[down2 * width + x];
                    float w = precipMm[rowOffset + left];
                    float e = precipMm[rowOffset + right];

                    // Stronger meridional (N-S) mixing eliminates 1-row horizontal banding behind isolated peaks.
                    tempBuffer[rowOffset + x] = 0.30f * c + 0.18f * (n1 + s1) + 0.08f * (n2 + s2) + 0.09f * (w + e);
                }
            });
            Array.Copy(tempBuffer, precipMm, totalCells);
        }

        // 4. Compute surface runoff via Budyko water balance and accumulate summary statistics (CL-4, CL-5).
        double landWeightSum = 0.0;
        double tempWeightedSum = 0.0;
        double precipWeightedSum = 0.0;
        double runoffWeightedSum = 0.0;
        double aridWeightSum = 0.0;
        double humidWeightSum = 0.0;
        double maxLandPrecip = 0.0;

        for (int y = 0; y < height; y++)
        {
            double w = cosWeight[y];
            int rowOffset = y * width;

            for (int x = 0; x < width; x++)
            {
                int i = rowOffset + x;
                float p = precipMm[i];
                float pet = petMm[i];

                if (elev[i] <= 0f)
                {
                    runoffMm[i] = 0f;
                    continue;
                }

                // Budyko-style nonlinear runoff partitioning:
                // In arid regions (P << PET), nearly all precipitation evaporates/infiltrates locally;
                // in humid/cold regions (P >= PET), excess water becomes surface river runoff.
                float aridityRatio = p / (p + pet + 1e-3f);
                float r = p * MathF.Pow(aridityRatio, 1.55f);

                // Cold/high-mountain snowmelt bonus (low evaporation on alpine peaks).
                if (tempC[i] < 4f && p > 250f)
                {
                    r = MathF.Max(r, p * 0.62f);
                }

                runoffMm[i] = r;

                landWeightSum += w;
                tempWeightedSum += tempC[i] * w;
                precipWeightedSum += p * w;
                runoffWeightedSum += r * w;
                if (p > maxLandPrecip) maxLandPrecip = p;

                if (p < 250f) aridWeightSum += w;
                else if (p >= 1000f) humidWeightSum += w;
            }
        }

        sw.Stop();

        return new ClimateResult
        {
            Source = source,
            TemperatureCelsius = tempC,
            PrecipitationMmPerYear = precipMm,
            PotentialEvaporationMmPerYear = petMm,
            RunoffMmPerYear = runoffMm,
            Settings = settings,
            MeanLandTemperatureCelsius = landWeightSum > 0 ? tempWeightedSum / landWeightSum : 0.0,
            MeanLandPrecipitationMm = landWeightSum > 0 ? precipWeightedSum / landWeightSum : 0.0,
            MaxLandPrecipitationMm = maxLandPrecip,
            MeanLandRunoffMm = landWeightSum > 0 ? runoffWeightedSum / landWeightSum : 0.0,
            AridLandFraction = landWeightSum > 0 ? aridWeightSum / landWeightSum : 0.0,
            HumidLandFraction = landWeightSum > 0 ? humidWeightSum / landWeightSum : 0.0,
            ElapsedSeconds = sw.Elapsed.TotalSeconds,
        };
    }

    private static void SweepZonalMoisture(
        ReadOnlySpan<float> rowElev,
        ReadOnlySpan<float> rowTemp,
        Span<float> outPrecip,
        Heightmap map,
        int y,
        int width,
        bool wrapsLongitude,
        bool leftToRight,
        float stepKm,
        float decayScaleKm,
        float orographicGain,
        float shadowStrength)
    {
        double baseLat = map.LatitudeAt(y);
        int upY = Math.Max(0, y - 3);
        int downY = Math.Min(map.Height - 1, y + 3);
        ReadOnlySpan<float> upElev = map.Elevations.AsSpan(upY * width, width);
        ReadOnlySpan<float> downElev = map.Elevations.AsSpan(downY * width, width);

        float moisture = 1250f;
        float prevSmoothedElev = 0f;
        float shadowMemory = 0f;

        int warmupSteps = wrapsLongitude ? width : 0;
        int tailBlendSteps = wrapsLongitude ? Math.Min(64, width / 8) : 0;
        int totalSteps = width + warmupSteps + tailBlendSteps;

        for (int s = 0; s < totalSteps; s++)
        {
            int x = leftToRight
                ? (s % width)
                : (width - 1 - (s % width));

            // Planetary Rossby-wave undulation (±5.5° latitude shift across longitude harmonics)
            // so climate belts curve organically rather than forming rigid horizontal lines.
            double lonRad = MathUtils.ToRadians(map.LongitudeAt(x));
            double waveShift = 4.2 * Math.Sin(2.0 * lonRad + 0.7) + 2.3 * Math.Cos(3.0 * lonRad - 1.1);
            float effectiveAbsLat = (float)Math.Abs(baseLat + waveShift);

            float itcz = 0.88f * MathF.Exp(-MathF.Pow(effectiveAbsLat / 12.5f, 2f));
            float subtropHigh = -0.52f * MathF.Exp(-MathF.Pow((effectiveAbsLat - 26.5f) / 9.5f, 2f));
            float midLatFront = 0.42f * MathF.Exp(-MathF.Pow((effectiveAbsLat - 50.0f) / 13.0f, 2f));
            float polarDry = -0.48f * MathUtils.SmoothStep(62f, 82f, effectiveAbsLat);
            float zonalFactor = Math.Clamp(0.92f + itcz + subtropHigh + midLatFront + polarDry, 0.22f, 1.95f);

            float h = rowElev[x];
            // Meridionally averaged barrier height so isolated 1-pixel peaks don't cast 1-row pencil shadows.
            float barrierH = 0.50f * MathF.Max(0f, h) + 0.25f * MathF.Max(0f, upElev[x]) + 0.25f * MathF.Max(0f, downElev[x]);
            float t = rowTemp[x];

            float tempCapacityScale = Math.Clamp(0.38f + 0.028f * (t + 15f), 0.22f, 1.55f);
            float samplePrecip;

            if (h <= 0f)
            {
                float targetOceanMoisture = 1480f * zonalFactor * tempCapacityScale;
                float rechargeRate = 1f - MathF.Exp(-stepKm / 380f);
                moisture = MathUtils.Lerp(moisture, targetOceanMoisture, rechargeRate);
                shadowMemory *= MathF.Exp(-stepKm / 200f);
                prevSmoothedElev = 0f;
                samplePrecip = moisture * 0.80f;
            }
            else
            {
                float dhMeters = barrierH - prevSmoothedElev;
                float slopeMPerKm = dhMeters / MathF.Max(1f, stepKm);
                prevSmoothedElev = barrierH;

                shadowMemory *= MathF.Exp(-stepKm / 420f);

                float orographicFactor = 1.0f;
                if (slopeMPerKm > 0.15f)
                {
                    float lift = Math.Clamp(slopeMPerKm / 18.0f, 0f, 2.5f);
                    orographicFactor = 1.0f + orographicGain * lift;
                }
                else if (slopeMPerKm < -0.15f)
                {
                    float descent = Math.Clamp(-slopeMPerKm / 16.0f, 0f, 2.0f);
                    shadowMemory = MathF.Max(shadowMemory, Math.Clamp(descent * shadowStrength, 0f, 0.82f));
                }

                float altitudeDryness = Math.Clamp(1.0f - (barrierH / 8_000f), 0.28f, 1.0f);
                float shadowSuppression = Math.Clamp(1.0f - shadowMemory * 0.76f, 0.16f, 1.0f);

                float localPrecip = moisture * zonalFactor * orographicFactor * shadowSuppression * altitudeDryness * 0.92f;
                samplePrecip = Math.Clamp(localPrecip, 15f, 6_500f);

                float effectiveDecayKm = (decayScaleKm * 1.25f) / (0.68f + 0.32f * orographicFactor);
                float retention = MathF.Exp(-stepKm / MathF.Max(160f, effectiveDecayKm));
                float continentalFloor = 195f * zonalFactor * tempCapacityScale * shadowSuppression;
                moisture = MathF.Max(continentalFloor, moisture * retention);
            }

            if (s >= warmupSteps && s < warmupSteps + width)
            {
                outPrecip[x] = samplePrecip;
            }
            else if (s >= warmupSteps + width)
            {
                float blend = (float)(s - (warmupSteps + width) + 1) / (tailBlendSteps + 1);
                outPrecip[x] = MathUtils.Lerp(samplePrecip, outPrecip[x], blend);
            }
        }
    }
}
