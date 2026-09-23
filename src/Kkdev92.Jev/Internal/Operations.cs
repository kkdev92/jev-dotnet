using Kkdev92.Jev.Wire;

namespace Kkdev92.Jev;

/// <summary>How an operation is named in messages: its method and path, both constants of the contract.</summary>
internal static class Operations
{
    public static string Describe(JevOperation operation) => operation switch
    {
        JevOperation.Evaluate => JevContract.SystemOneMethod + " " + JevContract.SystemOnePath,
        JevOperation.ListModels => JevContract.ModelsMethod + " " + JevContract.ModelsPath,
        _ => "a TypeSafe API operation",
    };

    /// <summary>The value of the <c>jev.operation</c> tag.</summary>
    public static string TagValue(JevOperation operation) => operation switch
    {
        JevOperation.Evaluate => "evaluate",
        JevOperation.ListModels => "models",
        _ => "unknown",
    };
}
