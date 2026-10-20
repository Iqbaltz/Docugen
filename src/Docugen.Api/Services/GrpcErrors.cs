using Docugen.Core.Exports;
using Docugen.Core.Reports;
using Docugen.Infrastructure.Exports;
using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using RpcStatus = Grpc.Core.Status;

namespace Docugen.Api.Services;

internal static class GrpcErrors
{
    public const string Domain = "docugen";

    /// <summary>The report, or NOT_FOUND.</summary>
    public static ReportDefinition FindReport(IReportCatalog catalog, string key) =>
        catalog.Find(key) ?? throw new RpcException(new RpcStatus(StatusCode.NotFound, $"There is no report named '{key}'."));

    /// <summary>INVALID_ARGUMENT with a readable message and a google.rpc.BadRequest listing each field.</summary>
    public static RpcException InvalidArgument(IReadOnlyList<BadRequest.Types.FieldViolation> violations)
    {
        var status = new Google.Rpc.Status
        {
            Code = (int)Code.InvalidArgument,
            Message = "Invalid request: " + string.Join("; ", violations.Select(v => $"{v.Field}: {v.Description}")),
            Details = { Any.Pack(new BadRequest { FieldViolations = { violations } }) },
        };
        return status.ToRpcException();
    }

    public static RpcException Busy(ExportBusyException exception) => new(new RpcStatus(StatusCode.ResourceExhausted, exception.Message));

    /// <summary>A failed export, with a google.rpc.ErrorInfo whose reason is the error code and metadata the export id.</summary>
    public static RpcException Failed(ExportFailedException exception)
    {
        var code = exception.Code switch
        {
            ExportErrorCodes.TooManyRows => Code.FailedPrecondition,
            ExportErrorCodes.QueryTimeout => Code.DeadlineExceeded,
            _ => Code.Internal,
        };
        var status = new Google.Rpc.Status
        {
            Code = (int)code,
            Message = exception.Message,
            Details =
            {
                Any.Pack(new ErrorInfo
                {
                    Reason = exception.Code,
                    Domain = Domain,
                    Metadata = { ["export_id"] = exception.ExportId.ToString() },
                }),
            },
        };
        return status.ToRpcException();
    }
}
