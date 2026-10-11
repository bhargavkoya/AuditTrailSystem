using System.Data;
using Dapper;

namespace AuditFlow.Workflow.Api.Data;

/// <summary>Maps DateOnly to the SQL Server DATE type for Dapper parameters and results.</summary>
public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public static void Register() => SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value.ToDateTime(TimeOnly.MinValue);
    }

    public override DateOnly Parse(object value) => value switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        _ => DateOnly.FromDateTime(Convert.ToDateTime(value))
    };
}
