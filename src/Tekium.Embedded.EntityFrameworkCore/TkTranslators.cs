// Copyright (c) Tekium. Licensed under the MIT License.

using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace Tekium.Embedded.EntityFrameworkCore;

/// <summary>The methods of .NET the provider knows how to write in SQL, besides those any relational provider does.</summary>
public sealed class TkMethodCallTranslatorProvider : RelationalMethodCallTranslatorProvider
{
    /// <summary>Initializes a new instance of the <see cref="TkMethodCallTranslatorProvider"/> class.</summary>
    /// <param name="dependencies">What a provider of translators needs.</param>
    public TkMethodCallTranslatorProvider(RelationalMethodCallTranslatorProviderDependencies dependencies)
        : base(dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        AddTranslators([new StringMethods(dependencies.SqlExpressionFactory)]);
    }

    /// <summary>StartsWith, EndsWith and Contains (comparing characters, as in .NET), ToLower and ToUpper.</summary>
    private sealed class StringMethods(ISqlExpressionFactory factory) : IMethodCallTranslator
    {
        public SqlExpression? Translate(
            SqlExpression? instance, MethodInfo method, IReadOnlyList<SqlExpression> arguments, IDiagnosticsLogger<DbLoggerCategory.Query> logger)
        {
            if (instance is null || method.DeclaringType != typeof(string))
            {
                return null;
            }

            var function = (method.Name, arguments.Count) switch
            {
                (nameof(string.StartsWith), 1) => "STARTSWITH",
                (nameof(string.EndsWith), 1) => "ENDSWITH",
                (nameof(string.Contains), 1) => "CONTAINS",
                (nameof(string.ToLower) or nameof(string.ToLowerInvariant), 0) => "LOWER",
                (nameof(string.ToUpper) or nameof(string.ToUpperInvariant), 0) => "UPPER",
                _ => null,
            };
            if (function is null || (arguments.Count == 1 && arguments[0].Type != typeof(string)))
            {
                return null;
            }

            if (arguments.Count == 0)
            {
                return factory.Function(function, [instance], nullable: true, argumentsPropagateNullability: [true], typeof(string), instance.TypeMapping);
            }

            var mapping = ExpressionExtensions.InferTypeMapping(instance, arguments[0]);
            return factory.Function(
                function,
                [factory.ApplyTypeMapping(instance, mapping), factory.ApplyTypeMapping(arguments[0], mapping)],
                nullable: true,
                argumentsPropagateNullability: [true, true],
                typeof(bool));
        }
    }
}

/// <summary>The properties of .NET the provider knows how to write in SQL: the length of a text.</summary>
public sealed class TkMemberTranslatorProvider : RelationalMemberTranslatorProvider
{
    /// <summary>Initializes a new instance of the <see cref="TkMemberTranslatorProvider"/> class.</summary>
    /// <param name="dependencies">What a provider of translators needs.</param>
    /// <param name="factory">Builds the expressions of SQL.</param>
    public TkMemberTranslatorProvider(RelationalMemberTranslatorProviderDependencies dependencies, ISqlExpressionFactory factory)
        : base(dependencies)
    {
        ArgumentNullException.ThrowIfNull(factory);
        AddTranslators([new StringLength(factory)]);
    }

    private sealed class StringLength(ISqlExpressionFactory factory) : IMemberTranslator
    {
        public SqlExpression? Translate(SqlExpression? instance, MemberInfo member, Type returnType, IDiagnosticsLogger<DbLoggerCategory.Query> logger) =>
            instance is not null && member.DeclaringType == typeof(string) && member.Name == nameof(string.Length)
                ? factory.Function("LENGTH", [instance], nullable: true, argumentsPropagateNullability: [true], typeof(int))
                : null;
    }
}

/// <summary>Writes a query as TekiumDB reads it: the limit of rows is LIMIT … OFFSET …, and texts are joined with ||.</summary>
/// <param name="dependencies">What a writer of queries needs.</param>
public sealed class TkQuerySqlGenerator(QuerySqlGeneratorDependencies dependencies) : QuerySqlGenerator(dependencies)
{
    /// <inheritdoc/>
    protected override void GenerateLimitOffset(SelectExpression selectExpression)
    {
        ArgumentNullException.ThrowIfNull(selectExpression);
        if (selectExpression.Limit is null && selectExpression.Offset is null)
        {
            return;
        }

        Sql.AppendLine().Append("LIMIT ");
        if (selectExpression.Limit is null)
        {
            Sql.Append("9223372036854775807");
        }
        else
        {
            Visit(selectExpression.Limit);
        }

        if (selectExpression.Offset is not null)
        {
            Sql.Append(" OFFSET ");
            Visit(selectExpression.Offset);
        }
    }

    /// <inheritdoc/>
    protected override string GetOperator(SqlBinaryExpression binaryExpression)
    {
        ArgumentNullException.ThrowIfNull(binaryExpression);
        return binaryExpression.OperatorType == ExpressionType.Add && binaryExpression.Type == typeof(string) ? " || " : base.GetOperator(binaryExpression);
    }
}

/// <summary>Which column type holds each CLR type, and how a value of each is written in SQL.</summary>
/// <param name="dependencies">What any source of type mappings needs.</param>
/// <param name="relationalDependencies">What a relational one needs.</param>
public sealed class TkTypeMappingSource(TypeMappingSourceDependencies dependencies, RelationalTypeMappingSourceDependencies relationalDependencies)
    : RelationalTypeMappingSource(dependencies, relationalDependencies)
{
    private static readonly Dictionary<Type, RelationalTypeMapping> ByType = new()
    {
        [typeof(bool)] = new BoolTypeMapping("BOOLEAN"),
        [typeof(byte)] = new ByteTypeMapping("TINYINT"),
        [typeof(short)] = new ShortTypeMapping("SMALLINT"),
        [typeof(int)] = new IntTypeMapping("INT"),
        [typeof(long)] = new LongTypeMapping("BIGINT"),
        [typeof(float)] = new FloatTypeMapping("REAL"),
        [typeof(double)] = new DoubleTypeMapping("DOUBLE"),
        [typeof(decimal)] = new DecimalTypeMapping("DECIMAL"),
        [typeof(string)] = new StringTypeMapping("TEXT", System.Data.DbType.String),
        [typeof(Guid)] = new GuidTypeMapping("UUID"),
        [typeof(DateTimeOffset)] = new Quoted<DateTimeOffset>("TIMESTAMP", "'{0:O}'"),
        [typeof(DateTime)] = new Quoted<DateTime>("DATETIME", "'{0:O}'"),
        [typeof(DateOnly)] = new Quoted<DateOnly>("DATE", "'{0:yyyy-MM-dd}'"),
        [typeof(TimeOnly)] = new Quoted<TimeOnly>("TIME", "'{0:HH:mm:ss.FFFFFFF}'"),
        [typeof(TimeSpan)] = new Quoted<TimeSpan>("INTERVAL", "'{0:c}'"),
        [typeof(byte[])] = new ByteArrayTypeMapping("BLOB"),
    };

    /// <inheritdoc/>
    protected override RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo)
    {
        if (mappingInfo.ClrType is { } type && ByType.TryGetValue(type, out var mapping))
        {
            return mapping;
        }

        if (mappingInfo.StoreTypeName is { } store)
        {
            return ByType.Values.FirstOrDefault(known => string.Equals(known.StoreType, store, StringComparison.OrdinalIgnoreCase));
        }

        return base.FindMapping(mappingInfo);
    }

    /// <summary>A date or a time: written in SQL as a text in quotes, which TekiumDB reads as the type it is compared with.</summary>
    private sealed class Quoted<T> : RelationalTypeMapping
    {
        private readonly string _format;

        public Quoted(string storeType, string format)
            : base(storeType, typeof(T)) => _format = format;

        private Quoted(RelationalTypeMappingParameters parameters, string format)
            : base(parameters) => _format = format;

        protected override string SqlLiteralFormatString => _format;

        protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) => new Quoted<T>(parameters, _format);
    }
}
