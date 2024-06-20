using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FastExpressionCompiler;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten.Internal;
using Marten.Linq;
using Marten.Linq.QueryHandlers;
using Marten.Linq.Selectors;
using Marten.Linq.SqlGeneration;
using Marten.Storage;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.SqlGeneration;

namespace Marten.Schema.Identity;

public class StrongTypedIdGeneration: ValueTypeInfo, IIdGeneration
{
    private readonly IScalarSelectClause _selector;

    private StrongTypedIdGeneration(Type outerType, PropertyInfo valueProperty, Type simpleType, ConstructorInfo ctor)
        : base(outerType, simpleType, valueProperty, ctor)
    {
        _selector = typeof(StrongTypedIdSelectClause<,>).CloseAndBuildAs<IScalarSelectClause>(this, OuterType, SimpleType);
    }

    private StrongTypedIdGeneration(Type outerType, PropertyInfo valueProperty, Type simpleType, MethodInfo builder)
        : base(outerType, simpleType, valueProperty, builder)
    {
        _selector = typeof(StrongTypedIdSelectClause<,>).CloseAndBuildAs<IScalarSelectClause>(this, OuterType, SimpleType);
    }

    public IEnumerable<Type> KeyTypes => Type.EmptyTypes;
    public bool RequiresSequences => false;

    public void GenerateCode(GeneratedMethod method, DocumentMapping mapping)
    {
        var document = new Use(mapping.DocumentType);

        if (SimpleType == typeof(Guid))
        {
            generateGuidWrapper(method, mapping, document);
        }
        else if (SimpleType == typeof(int))
        {
            generateIntWrapper(method, mapping, document);
        }
        else if (SimpleType == typeof(long))
        {
            generateLongWrapper(method, mapping, document);
        }
        else if (SimpleType == typeof(string))
        {
            generateStringWrapper(method, mapping, document);
        }
        else
        {
            throw new NotSupportedException();
        }

        method.Frames.Code($"return {{0}}.{mapping.CodeGen.AccessId};", document);
    }

    private void generateStringWrapper(GeneratedMethod method, DocumentMapping mapping, Use document)
    {
        method.Frames.Code($"return {{0}}.{mapping.IdMember.Name}.Value;", document);
    }

    private void generateLongWrapper(GeneratedMethod method, DocumentMapping mapping, Use document)
    {
        var database = Use.Type<IMartenDatabase>();
        if (Ctor != null)
        {
            method.Frames.Code(
                $"if ({{0}}.{mapping.IdMember.Name} == null) _setter({{0}}, new {OuterType.FullNameInCode()}({{1}}.Sequences.SequenceFor({{2}}).NextLong()));",
                document, database, mapping.DocumentType);
        }
        else
        {
            method.Frames.Code(
                $"if ({{0}}.{mapping.IdMember.Name} == null) _setter({{0}}, {OuterType.FullNameInCode()}.{Builder.Name}({{1}}.Sequences.SequenceFor({{2}}).NextLong()));",
                document, database, mapping.DocumentType);
        }
    }

    private void generateIntWrapper(GeneratedMethod method, DocumentMapping mapping, Use document)
    {
        var database = Use.Type<IMartenDatabase>();
        if (Ctor != null)
        {
            method.Frames.Code(
                $"if ({{0}}.{mapping.IdMember.Name} == null) _setter({{0}}, new {OuterType.FullNameInCode()}({{1}}.Sequences.SequenceFor({{2}}).NextInt()));",
                document, database, mapping.DocumentType);
        }
        else
        {
            method.Frames.Code(
                $"if ({{0}}.{mapping.IdMember.Name} == null) _setter({{0}}, {OuterType.FullNameInCode()}.{Builder.Name}({{1}}.Sequences.SequenceFor({{2}}).NextInt()));",
                document, database, mapping.DocumentType);
        }
    }

    private void generateGuidWrapper(GeneratedMethod method, DocumentMapping mapping, Use document)
    {
        var newGuid = $"{typeof(CombGuidIdGeneration).FullNameInCode()}.NewGuid()";
        var create = Ctor == null
            ? $"{OuterType.FullNameInCode()}.{Builder.Name}({newGuid})"
            : $"new {OuterType.FullNameInCode()}({newGuid})";

        method.Frames.Code(
            $"if ({{0}}.{mapping.IdMember.Name} == null) _setter({{0}}, {create});",
            document);
    }

    public ISelectClause BuildSelectClause(string tableName)
    {
        return _selector.CloneToOtherTable(tableName);
    }

    public static bool IsCandidate(Type idType, out StrongTypedIdGeneration? idGeneration)
    {
        if (idType.IsNullable())
        {
            idType = idType.GetGenericArguments().Single();
        }

        idGeneration = default;
        if (idType.IsClass && !idType.IsAbstract)
        {
            return false;
        }

        if (!idType.Name.EndsWith("Id"))
        {
            return false;
        }

        if (!idType.IsPublic && !idType.IsNestedPublic)
        {
            return false;
        }


        PropertyInfo[] properties;

        //If the id type is an F# discriminated union
        if (idType.IsClass && idType.IsAbstract)
        {
            var idProperty = idType.GetNestedTypes()
                .Where(x => x.IsSealed)
                .SingleOrDefaultIfMany()
                ?.GetProperties().SingleOrDefaultIfMany();

            if (idProperty == null || DocumentMapping.ValidIdTypes.Contains(idProperty.PropertyType) == false)
                return false;

            properties = [idProperty];

        }
        else
        {
            properties = idType.GetProperties().Where(x => DocumentMapping.ValidIdTypes.Contains(x.PropertyType))
                .ToArray();
        }

        if (properties.Length == 1)
        {
            var innerProperty = properties[0];
            var identityType = innerProperty.PropertyType;

            var ctor = idType.GetConstructors().FirstOrDefault(x =>
                x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == identityType);

            var dbType = PostgresqlProvider.Instance.GetDatabaseType(identityType, EnumStorage.AsInteger);
            var parameterType = PostgresqlProvider.Instance.TryGetDbType(identityType);

            if (ctor != null)
            {
                PostgresqlProvider.Instance.RegisterMapping(idType, dbType, parameterType);
                idGeneration = new StrongTypedIdGeneration(idType, innerProperty, identityType, ctor);
                return true;
            }

            var builder = idType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(x =>
                    x.ReturnType == idType && x.GetParameters().Length == 1 &&
                    x.GetParameters()[0].ParameterType == identityType);

            if (builder != null)
            {
                PostgresqlProvider.Instance.RegisterMapping(idType, dbType, parameterType);
                idGeneration = new StrongTypedIdGeneration(idType, innerProperty, identityType, builder);
                return true;
            }
        }


        return false;
    }

    public string ParameterValue(DocumentMapping mapping)
    {
        if (mapping.IdMember.GetRawMemberType().IsNullable())
        {
            return $"{mapping.IdMember.Name}.Value.{ValueProperty.Name}";
        }

        return $"{mapping.IdMember.Name}.{ValueProperty.Name}";
    }

    public void GenerateCodeForFetchingId(int index, GeneratedMethod sync, GeneratedMethod async,
        DocumentMapping mapping)
    {
        if (Builder != null)
        {
            sync.Frames.Code(
                $"var id = {OuterType.FullNameInCode()}.{Builder.Name}(reader.GetFieldValue<{SimpleType.FullNameInCode()}>({index}));");
            async.Frames.CodeAsync(
                $"var id = {OuterType.FullNameInCode()}.{Builder.Name}(await reader.GetFieldValueAsync<{SimpleType.FullNameInCode()}>({index}, token));");
        }
        else
        {
            sync.Frames.Code(
                $"var id = new {OuterType.FullNameInCode()}(reader.GetFieldValue<{SimpleType.FullNameInCode()}>({index}));");
            async.Frames.CodeAsync(
                $"var id = new {OuterType.FullNameInCode()}(await reader.GetFieldValueAsync<{SimpleType.FullNameInCode()}>({index}, token));");
        }
    }

    public Func<object, T> BuildInnerValueSource<T>()
    {
        var target = Expression.Parameter(typeof(object), "target");
        var method = ValueProperty.GetMethod;

        var callGetMethod = Expression.Call(Expression.Convert(target, OuterType), method);

        var lambda = Expression.Lambda<Func<object, T>>(callGetMethod, target);

        return lambda.CompileFast();
    }

    public void WriteBulkWriterCode(GeneratedMethod load, DocumentMapping mapping)
    {
        var dbType = PostgresqlProvider.Instance.ToParameterType(SimpleType);
        load.Frames.Code($"writer.Write(document.{mapping.IdMember.Name}.Value.{ValueProperty.Name}, {{0}});", dbType);
    }

    public void WriteBulkWriterCodeAsync(GeneratedMethod load, DocumentMapping mapping)
    {
        var dbType = PostgresqlProvider.Instance.ToParameterType(SimpleType);
        load.Frames.Code($"await writer.WriteAsync(document.{mapping.IdMember.Name}.Value.{ValueProperty.Name}, {{0}}, {{1}});", dbType, Use.Type<CancellationToken>());
    }
}

internal class StrongTypedIdSelectClause<TOuter, TInner>: ISelectClause, IScalarSelectClause, IModifyableFromObject,
    ISelector<TOuter>
{
    public StrongTypedIdSelectClause(StrongTypedIdGeneration idGeneration)
    {
        Converter = idGeneration.CreateConverter<TOuter, TInner>();
        MemberName = "d.id";
    }

    public StrongTypedIdSelectClause(Func<TInner, TOuter> converter)
    {
        Converter = converter;
    }

    public Func<TInner, TOuter> Converter { get; }

    public string MemberName { get; set; } = "d.id";

    public ISelectClause CloneToOtherTable(string tableName)
    {
        return new StrongTypedIdSelectClause<TOuter, TInner>(Converter)
        {
            FromObject = tableName, MemberName = MemberName
        };
    }

    public void ApplyOperator(string op)
    {
        MemberName = $"{op}({MemberName})";
    }

    public ISelectClause CloneToDouble()
    {
        throw new NotSupportedException();
    }

    public Type SelectedType => typeof(TOuter);

    public string FromObject { get; set; }

    public void Apply(ICommandBuilder sql)
    {
        if (MemberName.IsNotEmpty())
        {
            sql.Append("select ");
            sql.Append(MemberName);
            sql.Append(" as data from ");
        }

        sql.Append(FromObject);
        sql.Append(" as d");
    }

    public string[] SelectFields()
    {
        return new[] { MemberName };
    }

    public ISelector BuildSelector(IMartenSession session)
    {
        return this;
    }

    public IQueryHandler<TResult> BuildHandler<TResult>(IMartenSession session, ISqlFragment statement,
        ISqlFragment currentStatement)
    {
        if (typeof(TOuter).IsValueType)
        {
            var genericType = typeof(NullableListQueryHandler<>).MakeGenericType([typeof(TOuter)]);
            var nullableListQueryHandler = Activator.CreateInstance(genericType, [statement, this]);
            return (IQueryHandler<TResult>)nullableListQueryHandler;
        }
        else
        {
            return (IQueryHandler<TResult>)new ListQueryHandler<TOuter>(statement, this);
        }
    }

    public ISelectClause UseStatistics(QueryStatistics statistics)
    {
        return new StatsSelectClause<TOuter?>(this, statistics);
    }

    public TOuter? Resolve(DbDataReader reader)
    {
        var inner = reader.GetFieldValue<TInner>(0);
        return Converter(inner);
    }

    public async Task<TOuter?> ResolveAsync(DbDataReader reader, CancellationToken token)
    {
        var inner = await reader.GetFieldValueAsync<TInner>(0, token).ConfigureAwait(false);
        return Converter(inner);
    }

    public override string ToString()
    {
        return $"Data from {FromObject}";
    }
}
