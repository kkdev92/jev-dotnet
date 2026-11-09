using System.Globalization;

namespace Kkdev92.Jev.Requests;

/// <summary>
/// The checks that keep the images a request sends and the references in its text in step.
/// </summary>
/// <remarks>
/// <para>
/// TypeSafe refuses an image that nothing references with a 400. The opposite mistake — a reference
/// whose image is not sent — it cannot see: the reference arrives as text, and the answers are
/// about that text. Both are refused here, before anything is sent, for the references the SDK
/// makes; a string the SDK did not make is never mistaken for one.
/// </para>
/// <para>
/// The plan's own images were held to its questions when it was built. What is left to a call is
/// its state: the images passed with the call must each appear in it, and every reference in it
/// must be to an image the request sends.
/// </para>
/// </remarks>
internal static class RequestImages
{
    /// <summary>The images a call passes, checked, each once, leaving out the plan's own.</summary>
    /// <exception cref="ArgumentException">An entry is <see langword="null"/>.</exception>
    public static JevImage[] FromCall(IReadOnlyList<JevImage> images, JevDecisionPlan plan, string paramName)
    {
        if (images.Count == 0)
        {
            return [];
        }

        var result = new List<JevImage>(images.Count);

        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i] ?? throw new ArgumentException($"Image {i.ToString(CultureInfo.InvariantCulture)} is null.", paramName);

            if (Array.IndexOf(plan.Images, image) < 0 && !result.Contains(image))
            {
                result.Add(image);
            }
        }

        return [.. result];
    }

    /// <summary>Refuses a box question with no image to look at.</summary>
    /// <exception cref="ArgumentException">The plan asks a box question and neither it nor the call has an image.</exception>
    public static void CheckBoxes(JevDecisionPlan plan, JevImage[] callImages, string paramName)
    {
        if (plan.HasBoxes && plan.Images.Length == 0 && callImages.Length == 0)
        {
            throw new ArgumentException(
                "The plan asks a box question, which needs an image in the request. Pass the image with the call, or add it to the plan with AddImage.",
                paramName);
        }
    }

    /// <summary>Checks the references in a state given as content.</summary>
    /// <exception cref="ArgumentException">See <see cref="Check"/>.</exception>
    public static void CheckState(JevContent state, JevDecisionPlan plan, JevImage[] callImages)
    {
        if (state.Text is { } text)
        {
            if (callImages.Length == 0 && !ImageReferences.MayContain(text))
            {
                return;
            }

            var found = new HashSet<string>(StringComparer.Ordinal);
            ImageReferences.Collect(text, found);
            Check(found, plan, callImages);
        }
        else if (state.Json is { } json)
        {
            CheckState(json, plan, callImages);
        }
    }

    /// <summary>Checks the references in a state given as the JSON it was written as.</summary>
    /// <exception cref="ArgumentException">See <see cref="Check"/>.</exception>
    public static void CheckState(ReadOnlySpan<byte> json, JevDecisionPlan plan, JevImage[] callImages)
    {
        if (callImages.Length == 0 && !ImageReferences.MayContain(json))
        {
            return;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        ImageReferences.CollectFromJson(json, found);
        Check(found, plan, callImages);
    }

    /// <exception cref="ArgumentException">
    /// An image passed with the call appears nowhere in the state, or the state references an image
    /// the request does not send.
    /// </exception>
    private static void Check(HashSet<string> found, JevDecisionPlan plan, JevImage[] callImages)
    {
        foreach (var image in callImages)
        {
            if (!found.Remove(image.Reference))
            {
                throw new ArgumentException(
                    "An image passed with the call appears nowhere in the state. Write its Reference where the image belongs in the state, or, if a question shows it, add it to the plan with AddImage instead.",
                    "images");
            }
        }

        foreach (var image in plan.Images)
        {
            found.Remove(image.Reference);
        }

        if (found.Count > 0)
        {
            throw new ArgumentException(
                "The state contains the reference of an image this request does not send, so the service would read the reference as text. Pass that image with the call.",
                "state");
        }
    }
}
