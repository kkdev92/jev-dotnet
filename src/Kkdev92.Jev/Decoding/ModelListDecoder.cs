using System.Text.Json;
using Kkdev92.Jev.Wire;

namespace Kkdev92.Jev.Decoding;

/// <summary>Decodes <c>GET /v1/models</c> through the generated wire model.</summary>
/// <remarks>
/// A small, rarely called response with nothing to gain from a handwritten reader, so the
/// source-generated path is the only one.
/// </remarks>
internal static class ModelListDecoder
{
    public static IReadOnlyList<JevModel> Decode(ReadOnlySpan<byte> body, string? requestId)
    {
        ModelMetadataList? list;

        try
        {
            list = JsonSerializer.Deserialize(body, JevWireJsonContext.Default.ModelMetadataList);
        }
        catch (JsonException)
        {
            throw new JevProtocolException(JevOperation.ListModels, JevProtocolError.MalformedJson, requestId);
        }

        if (list is null)
        {
            throw new JevProtocolException(JevOperation.ListModels, JevProtocolError.UnexpectedShape, requestId);
        }

        var validation = new WireValidation();
        list.Validate(validation, "$");

        if (!validation.IsValid)
        {
            throw new JevProtocolException(JevOperation.ListModels, JevProtocolError.UnexpectedShape, requestId);
        }

        var models = new JevModel[list.Models.Count];

        for (var i = 0; i < models.Length; i++)
        {
            var model = list.Models[i];
            models[i] = new JevModel(model.Name, model.Description, model.ReleaseDate);
        }

        return models;
    }
}
