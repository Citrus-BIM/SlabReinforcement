using System.Diagnostics;
using System.Threading;
using SlabReinforcement;

static void VerifyCadImportUsesTotalTransform()
{
    CadTransform3D instanceTransform = new CadTransform3D(
        new CadPoint3D(10, 20, 0),
        new CadPoint3D(1, 0, 0),
        new CadPoint3D(0, 1, 0),
        new CadPoint3D(0, 0, 1));

    CadTransform3D totalTransform = new CadTransform3D(
        new CadPoint3D(100, 200, 0),
        new CadPoint3D(2, 0, 0),
        new CadPoint3D(0, 2, 0),
        new CadPoint3D(0, 0, 1));

    CadTransform3D effectiveTransform = CadImportPlacement.GetEffectiveTransform(instanceTransform, totalTransform);
    CadPoint3D actual = CadImportPlacement.TransformVertex(new CadPoint3D(1, 1, 0), effectiveTransform);

    ExpectClose(actual.X, 102, "CAD import should respect total transform scale and X offset.");
    ExpectClose(actual.Y, 202, "CAD import should respect total transform scale and Y offset.");
    ExpectClose(actual.Z, 0, "CAD import should preserve Z for planar input.");
}

static void VerifyCadImportDoesNotDoubleScaleWhenTransformAlreadyContainsMillimeters()
{
    const double millimetersToFeet = 1.0 / 304.8;

    CadTransform3D totalTransform = new CadTransform3D(
        new CadPoint3D(10, 20, 0),
        new CadPoint3D(millimetersToFeet, 0, 0),
        new CadPoint3D(0, millimetersToFeet, 0),
        new CadPoint3D(0, 0, 1));

    CadPoint3D actual = CadImportPlacement.TransformVertex(new CadPoint3D(1000, 0, 0), totalTransform);
    ExpectClose(actual.X, 10 + 1000 * millimetersToFeet, "CAD import should rely on transform scale instead of applying units twice.");
    ExpectClose(actual.Y, 20, "CAD import should preserve Y when source Y is zero.");
}

static void VerifyReturnsImmediatelyForLongOperation()
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    string? warning = StartupAsyncRunner.Start(
        () => Task.Delay(TimeSpan.FromSeconds(10)),
        TimeSpan.FromMilliseconds(50),
        _ => { });

    stopwatch.Stop();

    if (warning != null)
    {
        throw new Exception($"Unexpected startup warning: {warning}");
    }

    if (stopwatch.Elapsed > TimeSpan.FromSeconds(1))
    {
        throw new Exception($"Startup runner blocked too long: {stopwatch.Elapsed}.");
    }
}

static async Task VerifyBackgroundActionRuns()
{
    TaskCompletionSource<bool> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int executionCount = 0;

    string? warning = StartupAsyncRunner.Start(
        async () =>
        {
            await Task.Yield();
            Interlocked.Increment(ref executionCount);
            completed.TrySetResult(true);
        },
        TimeSpan.FromSeconds(2),
        _ => { });

    if (warning != null)
    {
        throw new Exception($"Unexpected startup warning: {warning}");
    }

    Task finished = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(2)));
    if (finished != completed.Task)
    {
        throw new Exception("Background action did not complete in time.");
    }

    if (executionCount != 1)
    {
        throw new Exception($"Expected background action to run once, but got {executionCount}.");
    }
}

static void ExpectClose(double actual, double expected, string message)
{
    if (Math.Abs(actual - expected) > 1e-9)
    {
        throw new Exception($"{message} Expected {expected}, got {actual}.");
    }
}

VerifyCadImportUsesTotalTransform();
VerifyCadImportDoesNotDoubleScaleWhenTransformAlreadyContainsMillimeters();
VerifyReturnsImmediatelyForLongOperation();
await VerifyBackgroundActionRuns();
Console.WriteLine("Startup and CAD transform smoke tests passed.");
