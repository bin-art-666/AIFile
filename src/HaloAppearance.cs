namespace AIFile;

// Visual defaults of the Halo 0.4 desktop. AIFile has its own preferences and data.
internal sealed record HaloAppearance(int BackplateRadius = 20, int BackplateTransparency = 75,
    int BackplateBlur = 2, string ButtonColor = HaloAppearance.DefaultButtonColor,
    string SelectedButtonColor = "#FF32D4EB", bool ShowInteractionBorders = false,
    int NoticeSeconds = 4, int ErrorSeconds = 8, DragPreviewMode DragPreview = DragPreviewMode.GrabPoint,
    int DragDistance = DragPreviewPlacement.DefaultDistance, int IconSize = 36, int HorizontalGap = 12, int VerticalGap = 8)
{
    internal const string DefaultButtonColor = "#1C14DDFF", FolderColor = "#0099EC";
    internal const int TransparencyReference = 17;

    internal static uint ColorValue(string value, uint fallback) => value.Length == 0 ? fallback
        : Convert.ToUInt32(value[1..], 16) | (value.Length == 7 ? 0xFF000000u : 0u);

    internal static bool ValidColor(string? value) => value is { Length: 7 or 9 } && value[0] == '#'
        && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    internal bool IsValid => BackplateRadius is >= 12 and <= 40 && BackplateTransparency is >= 0 and <= 80
        && BackplateBlur is >= 0 and <= 100 && NoticeSeconds is >= 1 and <= 30 && ErrorSeconds is >= 1 and <= 30
        && ValidColor(ButtonColor) && ValidColor(SelectedButtonColor) && Enum.IsDefined(DragPreview)
        && DragDistance is >= 0 and <= DragPreviewPlacement.MaximumDistance
        && IconSize is >= 32 and <= 96 && HorizontalGap is >= 4 and <= 32 && VerticalGap is >= 4 and <= 32;
}
