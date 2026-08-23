using Cordango.Standalone.Data;

namespace {{AppNamespace}}.Data;

/// <summary>
/// What {{AppName}} contains, as JSON Schema — the one description its OpenAPI document and its MCP
/// server both answer from.
///
/// <para>Empty until entities are generated. Regenerating replaces this file with the real thing,
/// worked out by the compiler from the same manifest that produced the entities beside it.</para>
/// </summary>
public static class AppSchema
{
    public static readonly AppSchemaCatalogue Catalogue = new(
        "{{AppKey}}",
        "{{AppName}}",
        "0.1.0",
        null,
        [],
        []);
}
