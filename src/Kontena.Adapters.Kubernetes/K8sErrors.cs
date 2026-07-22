using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using k8s.Autorest;
using Kontena.Core.Errors;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// Translates what the Kubernetes client throws into Kontena's own failure vocabulary, so the shell
/// can tell "the cluster is not answering" from "your credentials are no longer good" without
/// knowing anything about k8s. The Docker adapter has done this since the start; a cluster that
/// falls over deserves the same treatment.
/// </summary>
internal static class K8sErrors
{
    /// <param name="context">The kube-context, so a message can name what it could not reach.</param>
    public static EngineException Map(Exception ex, string context) => ex switch
    {
        EngineException already => already,

        // An expired token or a rotated certificate looks exactly like this, and it is the single
        // most common reason a cluster that worked yesterday does not work today.
        HttpOperationException { Response.StatusCode: HttpStatusCode.Unauthorized } http =>
            new EnginePermissionException(
                $"'{context}' rejected the credentials in your kubeconfig. A token or client certificate may have expired.",
                http),

        HttpOperationException { Response.StatusCode: HttpStatusCode.Forbidden } http =>
            new EnginePermissionException(
                $"Your user is authenticated against '{context}' but not allowed to do this.", http),

        HttpOperationException { Response.StatusCode: HttpStatusCode.NotFound } http =>
            new ResourceNotFoundException(http.Message, http),

        HttpOperationException http => new EngineException(
            $"'{context}' returned {(int)http.Response.StatusCode} {http.Response.ReasonPhrase}.", http),

        // A cluster behind an expired or untrusted certificate: reachable, but not trustable.
        AuthenticationException auth => new EnginePermissionException(
            $"The TLS certificate '{context}' presented was not accepted. It may have expired, or the CA in your kubeconfig no longer matches.",
            auth),

        TimeoutException => new EngineUnreachableException(
            $"'{context}' did not answer in time.", ex),

        HttpRequestException or SocketException or IOException => new EngineUnreachableException(
            $"Cannot reach the apiserver for '{context}'. The cluster may be stopped, or unreachable from this network.",
            ex),

        _ => new EngineException(ex.Message, ex),
    };
}
