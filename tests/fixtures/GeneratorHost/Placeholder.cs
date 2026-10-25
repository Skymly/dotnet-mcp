namespace GeneratorHost;

public static class Placeholder
{
    public static string Who() => SampleApp.Generated.CustomMarker.Who;
}

public partial class PartialThing
{
    // Handwritten implementation of the generated partial definition
    // (generation-trigger seam, issue #524).
    partial void OnValueChanged(int value)
    {
    }

    // Handwritten *definition* of the generated partial implementation
    // (the GeneratedRegex shape — the trigger side of the pair).
    partial void GeneratedImpl();
}
