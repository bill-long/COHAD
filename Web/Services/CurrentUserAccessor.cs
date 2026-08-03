#nullable enable
using System.Security.Claims;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    /// <summary>
    /// The user document for whoever is making the current request, read at most once per request.
    /// <para>
    /// Authorization already reads this document to evaluate a role policy, and most endpoints then
    /// read it again to do their own work - two point reads of the same Cosmos item per request.
    /// Registering this scoped and routing both through it collapses them to one, including on the
    /// endpoints no role policy guards (where nothing would have read it first).
    /// </para>
    /// </summary>
    public interface ICurrentUserAccessor
    {
        /// <summary>
        /// The caller's user document, or null when the token carries no usable identity claims or no
        /// user matches them. Repeat calls within one request return the first result, including when
        /// that result was "no such user"; only a failed read is retried.
        /// </summary>
        Task<User?> GetAsync(ClaimsPrincipal? principal);

        /// <summary>
        /// The unique id <see cref="GetAsync"/> would look up for <paramref name="principal"/>, or null
        /// when the token carries no usable identity claims. Exposed so callers that report a failure
        /// name the id that was actually read, rather than deriving their own copy that can drift.
        /// </summary>
        string? TryGetUniqueId(ClaimsPrincipal? principal);
    }

    /// <inheritdoc />
    internal sealed class CurrentUserAccessor : ICurrentUserAccessor
    {
        private readonly IUserRepository _userRepository;

        /// <summary>
        /// The in-flight or completed lookup, keyed by the identity it was made for. The task itself is
        /// cached rather than its result, so two callers that ask before the first read completes share
        /// it instead of starting two. Guarded by <see cref="_gate"/>: a request can await two things
        /// at once that both ask for the caller.
        /// </summary>
        private readonly object _gate = new();
        private string? _cachedUniqueId;
        private Task<User?>? _cachedLookup;

        public CurrentUserAccessor(IUserRepository userRepository)
        {
            _userRepository = userRepository;
        }

        public Task<User?> GetAsync(ClaimsPrincipal? principal)
        {
            var uniqueId = TryGetUniqueId(principal);
            if (uniqueId == null)
                return Task.FromResult<User?>(null);

            TaskCompletionSource<User?> completion;
            lock (_gate)
            {
                // Keyed rather than unconditional: a scope serves one caller in practice, but returning
                // some other principal's user would be an authorization bug, not a stale cache.
                if (_cachedLookup != null && _cachedUniqueId == uniqueId)
                    return _cachedLookup;

                // Published before the read starts, so that a repository which completes synchronously
                // (the in-memory one) cannot run the forget-on-failure path before there is anything to
                // forget, which would leave a cached task paired with no id.
                completion = new TaskCompletionSource<User?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _cachedUniqueId = uniqueId;
                _cachedLookup = completion.Task;
            }

            // Started outside the lock: the repository call runs synchronously up to its first await,
            // and that should not happen while another caller is blocked on the gate.
            _ = LoadAsync(completion, uniqueId);
            return completion.Task;
        }

        /// <summary>
        /// Reads the user into <paramref name="completion"/>, and forgets the attempt if it failed.
        /// <para>
        /// A miss is kept, because "no user matches this token" is an answer and repeating the read
        /// for it would break the once-per-request contract. A failure is not an answer: keeping a
        /// faulted task would turn one transient Cosmos error into a guaranteed failure for every
        /// later caller in the request, where before each did its own read and could succeed.
        /// </para>
        /// <para>
        /// Cancellation is completed as cancelled rather than faulted, per the repo's broad-catch
        /// rule - a caller that navigated away is not an application error. The completion source is
        /// always finished on every path: leaving it pending would hang everything sharing the memo.
        /// </para>
        /// </summary>
        private async Task LoadAsync(TaskCompletionSource<User?> completion, string uniqueId)
        {
            try
            {
                completion.SetResult(await _userRepository.GetByUniqueIdAsync(uniqueId));
            }
            catch (System.OperationCanceledException ex)
            {
                Forget(completion.Task);
                completion.SetCanceled(ex.CancellationToken);
            }
            catch (System.Exception ex)
            {
                Forget(completion.Task);
                completion.SetException(ex);
            }
        }

        /// <summary>
        /// Drops <paramref name="lookup"/> from the memo if it is still the current one. The identity
        /// check matters: a slow failing lookup for one principal must not evict a later, successful
        /// lookup that replaced it.
        /// </summary>
        private void Forget(Task<User?> lookup)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cachedLookup, lookup))
                {
                    _cachedUniqueId = null;
                    _cachedLookup = null;
                }
            }
        }

        /// <summary>
        /// The caller's unique id, or null when the required claims are absent.
        /// <see cref="User.GetUniqueIdFromClaims"/> throws in that case; an unauthenticated or
        /// partially-claimed request is a normal condition here, not an exceptional one.
        /// <para>
        /// Deliberately keyed on the claims alone, not on <c>Identity.IsAuthenticated</c>. An
        /// anonymous principal carries no NameIdentifier, so the parse already answers null for it
        /// without a read - while an IsAuthenticated precondition would look at the primary identity
        /// only, and answer null for a correctly signed-in caller if anything ever attaches a second
        /// one. That failure mode is a site-wide 403, so it is not worth guarding against a cost the
        /// parse already avoids.
        /// </para>
        /// </summary>
        public string? TryGetUniqueId(ClaimsPrincipal? principal)
        {
            if (principal == null)
                return null;

            try
            {
                return User.GetUniqueIdFromClaims(principal.Claims);
            }
            catch (System.InvalidOperationException)
            {
                return null;
            }
        }
    }
}
