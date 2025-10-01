using System.IO;
using KoalaWiki.KoalaWarehouse;
using KoalaWiki.KoalaWarehouse.GenerateThinkCatalogue;
using Newtonsoft.Json;
using Xunit;

namespace KoalaWiki.Tests.GenerateThinkCatalogue;

public class CatalogueFunctionTests
{
    [Fact]
    public void GenerateCatalogue_WithValidInput_StoresContent()
    {
        var catalogueFunction = new CatalogueFunction();
        var catalogue = new DocumentResultCatalogue
        {
            items =
            [
                new DocumentResultCatalogueItem
                {
                    name = "root",
                    title = "Root",
                    prompt = "Describe the project entry point."
                }
            ]
        };

        var result = catalogueFunction.GenerateCatalogue(catalogue);

        Assert.True(catalogueFunction.CatalogueGenerated);
        Assert.Equal(JsonConvert.SerializeObject(catalogue, Formatting.None), catalogueFunction.Content);
        Assert.Single(result.items);
    }

    [Fact]
    public void GenerateCatalogue_MissingPrompt_Throws()
    {
        var catalogueFunction = new CatalogueFunction();
        var catalogue = new DocumentResultCatalogue
        {
            items =
            [
                new DocumentResultCatalogueItem
                {
                    name = "root",
                    title = "Root",
                    prompt = string.Empty
                }
            ]
        };

        Assert.Throws<InvalidDataException>(() => catalogueFunction.GenerateCatalogue(catalogue));
    }

    [Fact]
    public void GenerateCatalogue_CalledTwice_Throws()
    {
        var catalogueFunction = new CatalogueFunction();
        var catalogue = new DocumentResultCatalogue
        {
            items =
            [
                new DocumentResultCatalogueItem
                {
                    name = "root",
                    title = "Root",
                    prompt = "Describe the project entry point."
                }
            ]
        };

        catalogueFunction.GenerateCatalogue(catalogue);

        Assert.Throws<InvalidOperationException>(() => catalogueFunction.GenerateCatalogue(catalogue));
    }
}
