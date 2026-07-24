namespace Vomplayer.Wayland;

// Raw observation of an output's preferred image description, as forwarded by the shim's per-output probe (see hdr_helper.c's vompl_output_image_info_fn). All values are wp_color_management_v1 wire units: TfNamed / PrimariesNamed are the protocol enums; MinLum is 1/10000 cd/m² while MaxLum / RefLum are plain cd/m² (that unit asymmetry is the protocol's, preserved raw). Each Has* flag is false when the corresponding event did not arrive before the info `done` (e.g. the compositor described the TF via tf_power instead of tf_named).
//
// PrimariesNamed is carried for diagnostics only and must never influence classification — wide-gamut SDR panels exist, so BT.2020 primaries are not an HDR signal.
public readonly record struct OutputImageDescription(
    bool HasTfNamed, uint TfNamed,
    bool HasPrimariesNamed, uint PrimariesNamed,
    bool HasLuminances, uint MinLum, uint MaxLum, uint RefLum);

// Which rule classified the output as HDR; None means neither fired (⇒ SDR). Surfaced in the diagnostic overlay so a future compositor behavior change is diagnosable at a glance.
public enum HdrJustification
{
    None,
    TransferFunction,
    LuminanceHeadroom,
}

// Classifies an output's preferred image description as HDR or SDR. Pure function, in C# (not in the shim) per the "Native code is for ABI interop only" rule: the shim forwards the raw observations; classification is policy.
//
// Two independent signals, either of which means HDR:
//  - Transfer function: tf_named is PQ or HLG (enum values mirror wp_color_manager_v1.transfer_function in color-management-v1-client-protocol.h). The unambiguous legacy signal — pre-6.6-ish KWin advertised PQ for HDR-enabled outputs.
//  - Luminance headroom: max_lum strictly above reference_lum. This is how modern KWin (observed 6.6.5) expresses HDR-enabled: the preferred description's tf is gamma22 regardless, and headroom above SDR-white carries the HDR-ness (e.g. max=390 ref=201 on an HDR-enabled 400-nit panel vs max=200 ref=200 exactly on SDR outputs — hence strict >).
public static class HdrClassifier
{
    public const uint TransferFunctionSt2084Pq = 11;
    public const uint TransferFunctionHlg = 13;

    public static HdrJustification Classify(OutputImageDescription d)
    {
        if (d.HasTfNamed && (d.TfNamed == TransferFunctionSt2084Pq || d.TfNamed == TransferFunctionHlg))
        {
            return HdrJustification.TransferFunction;
        }
        if (d.HasLuminances && d.MaxLum > d.RefLum)
        {
            return HdrJustification.LuminanceHeadroom;
        }
        return HdrJustification.None;
    }

    public static bool IsHdr(OutputImageDescription d)
    {
        return Classify(d) != HdrJustification.None;
    }
}
