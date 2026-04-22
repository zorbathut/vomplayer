namespace Vomplayer.Wayland;

// Classifies a wp_image_description transfer function as HDR or SDR. Pure function, in C# (not in the shim) per the "Native code is for ABI interop only" rule: the shim forwards the raw tf_named observation; classification is policy.
//
// Enum values mirror wp_color_manager_v1.transfer_function in color-management-v1-client-protocol.h. Only PQ and HLG map to HDR; every other named transfer function (sRGB, BT.1886, gamma22, etc.) is SDR. A missing tf_named event (e.g. the compositor described the output with tf_power instead) also resolves to "not HDR" — no reliably-HDR transfer function uses tf_power.
public static class HdrClassifier
{
    public const uint TransferFunctionSt2084Pq = 11;
    public const uint TransferFunctionHlg = 13;

    public static bool IsHdr(bool hasTfNamed, uint tfNamed)
    {
        if (!hasTfNamed)
        {
            return false;
        }
        return tfNamed == TransferFunctionSt2084Pq || tfNamed == TransferFunctionHlg;
    }
}
