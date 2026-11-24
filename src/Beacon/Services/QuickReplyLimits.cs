namespace Beacon.Services;

/// <summary>
/// Measured quick-reply layout limits. Buttons live in a wrapping WrapPanel
/// inside the chat card, so the safe count and label length depend on the
/// card's actual width (which already follows screen size and DPI scaling).
/// Values are device-independent pixels, the same unit WPF layout uses.
/// </summary>
public sealed record QuickReplyLimits(int MaxButtons, int MaxLabelChars)
{
    // QuickReplyBar has Padding 14,10; QuickReplyButton has Padding 12,7,
    // FontSize 12 and an 8px right margin. At 12px Segoe UI a label measures
    // about 7.4 px per character plus ~25 px of padding/border.
    private const double LabelPixelWidth = 7.4;
    private const double LabelFixedWidth = 26;
    private const double ShortButtonWidth = 68;

    /// <summary>
    /// Limits for a given chat-panel width in DIPs. Small screens yield fewer,
    /// shorter buttons; wide screens yield more. Bounded so any screen stays
    /// readable and no payload can flood the card.
    /// </summary>
    public static QuickReplyLimits ForPanelWidth(double panelWidth)
    {
        var inner = Math.Max(120, panelWidth - 28);

        var labelChars = (int)Math.Floor((inner - LabelFixedWidth) / LabelPixelWidth);
        labelChars = Math.Clamp(labelChars, 18, 72);

        var perRow = Math.Max(2, (int)Math.Floor(inner / ShortButtonWidth));
        var maxButtons = Math.Clamp(perRow * 2, 4, 8);

        return new QuickReplyLimits(maxButtons, labelChars);
    }

    /// <summary>
    /// Estimates the chat-panel width before layout has happened. Mirrors
    /// NotificationWindow.FitOverlay's 36% chat share.
    /// </summary>
    public static QuickReplyLimits Estimate(double screenWidth)
    {
        var available = Math.Max(360, screenWidth - 48);
        var chatWidth = Math.Clamp(available * 0.36, 220, 520);
        return ForPanelWidth(chatWidth);
    }

    /// <summary>Truncates an overlong label with an ellipsis, keeping the reply value untouched.</summary>
    public string Fit(string label)
    {
        if (string.IsNullOrEmpty(label) || label.Length <= MaxLabelChars) return label;
        return label[..Math.Max(1, MaxLabelChars - 1)] + "…";
    }
}
