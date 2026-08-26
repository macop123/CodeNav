using CodeNav.OutOfProc.Constants;
using SqlDocumentMapper = CodeNav.OutOfProc.Languages.Sql.Mappers.DocumentMapper;

namespace CodeNav.Test;

[TestFixture]
internal class SqlDocumentMapperTests
{
    [Test]
    public async Task MapsCreateProcedure()
    {
        const string source = """
            CREATE PROCEDURE dbo.GetUser (@id int)
            AS
            BEGIN
                SELECT * FROM Users WHERE Id = @id
            END
            GO
            """;

        var codeItems = await SqlDocumentMapper.MapDocument(source, new(), cancellationToken: default);

        var procedure = codeItems.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(procedure.Kind, Is.EqualTo(CodeItemKindEnum.Procedure));
            Assert.That(procedure.Name, Is.EqualTo("GetUser"));
        }
    }

    [Test]
    public async Task MapsMultipleBatchesAsFlatList()
    {
        const string source = """
            CREATE VIEW dbo.ActiveUsers AS SELECT * FROM Users WHERE Active = 1
            GO
            CREATE TABLE dbo.Orders (Id INT PRIMARY KEY, UserId INT)
            GO
            CREATE PROCEDURE dbo.GetUser (@id int) AS SELECT * FROM Users WHERE Id = @id
            GO
            """;

        var codeItems = await SqlDocumentMapper.MapDocument(source, new(), cancellationToken: default);

        Assert.That(codeItems.Select(item => (item.Name, item.Kind)), Is.EqualTo(new[]
        {
            ("ActiveUsers", CodeItemKindEnum.View),
            ("Orders", CodeItemKindEnum.Table),
            ("GetUser", CodeItemKindEnum.Procedure),
        }));
    }

    [Test]
    public async Task MapsProcedureWithoutBeginEnd()
    {
        const string source = "CREATE PROCEDURE X AS SELECT 1";

        var codeItems = await SqlDocumentMapper.MapDocument(source, new(), cancellationToken: default);

        var procedure = codeItems.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(procedure.Kind, Is.EqualTo(CodeItemKindEnum.Procedure));
            Assert.That(procedure.Name, Is.EqualTo("X"));
            Assert.That(procedure.Span.End, Is.EqualTo(source.Length));
        }
    }

    [Test]
    public async Task MapsAlterAndCreateOrAlterProcedure()
    {
        const string source = """
            ALTER PROCEDURE dbo.First AS SELECT 1
            GO
            CREATE OR ALTER PROCEDURE dbo.Second AS SELECT 2
            GO
            """;

        var codeItems = await SqlDocumentMapper.MapDocument(source, new(), cancellationToken: default);

        Assert.That(codeItems.Select(item => (item.Name, item.Kind)), Is.EqualTo(new[]
        {
            ("First", CodeItemKindEnum.Procedure),
            ("Second", CodeItemKindEnum.Procedure),
        }));
    }

    [Test]
    public async Task MapsAseStyleScriptInvalidForMssql()
    {
        // Owner-qualified name, "select into" temp table and "holdlock" are all valid ASE but
        // would not parse as (or would be flagged by) an MSSQL-only parser; the batch ends with
        // ASE's "go 3" repeat-count suffix instead of a plain "go".
        const string source = """
            create procedure sa.old_report as
            select * into #tmp from Orders holdlock
            select * from #tmp
            go 3
            """;

        var codeItems = await SqlDocumentMapper.MapDocument(source, new(), cancellationToken: default);

        var procedure = codeItems.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(procedure.Kind, Is.EqualTo(CodeItemKindEnum.Procedure));
            Assert.That(procedure.Name, Is.EqualTo("old_report"));
        }
    }

    [Test]
    public async Task MapsQuotedAndBracketedNames()
    {
        const string source = """
            CREATE TABLE [dbo].[Order Items] (Id INT PRIMARY KEY)
            GO
            CREATE VIEW "OrderView" AS SELECT * FROM Orders
            GO
            """;

        var codeItems = await SqlDocumentMapper.MapDocument(source, new(), cancellationToken: default);

        Assert.That(codeItems.Select(item => (item.Name, item.Kind)), Is.EqualTo(new[]
        {
            ("Order Items", CodeItemKindEnum.Table),
            ("OrderView", CodeItemKindEnum.View),
        }));
    }

    [Test]
    public async Task DoesNotMapCreateInsideStringLiteral()
    {
        const string source = "EXEC('CREATE PROCEDURE FakeOne AS SELECT 1')";

        var codeItems = await SqlDocumentMapper.MapDocument(source, new(), cancellationToken: default);

        Assert.That(codeItems, Is.Empty);
    }

    [Test]
    public async Task MapsMultipleTablesInOneBatchWithoutGo()
    {
        const string source =
            "CREATE TABLE dbo.Foo (Id INT PRIMARY KEY) CREATE TABLE dbo.Bar (Id INT PRIMARY KEY)";

        var codeItems = await SqlDocumentMapper.MapDocument(source, new(), cancellationToken: default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(codeItems.Select(item => (item.Name, item.Kind)), Is.EqualTo(new[]
            {
                ("Foo", CodeItemKindEnum.Table),
                ("Bar", CodeItemKindEnum.Table),
            }));
            Assert.That(codeItems[0].Span.End, Is.LessThanOrEqualTo(codeItems[1].Span.Start));
        }
    }

    [Test]
    public async Task MapsMultipleAlterTableStatementsInOneBatch()
    {
        const string source =
            "ALTER TABLE dbo.Foo ADD Bar INT NULL; ALTER TABLE dbo.Foo ADD Baz INT NULL;";

        var codeItems = await SqlDocumentMapper.MapDocument(source, new(), cancellationToken: default);

        Assert.That(codeItems, Has.Count.EqualTo(2));
        Assert.That(codeItems.All(item => item.Kind == CodeItemKindEnum.Table), Is.True);
        Assert.That(codeItems[0].Span.End, Is.LessThanOrEqualTo(codeItems[1].Span.Start));
    }
}
