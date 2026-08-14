using System.Collections.Generic;

namespace Cuvara.Netcode.Snapshot
{
    /// <summary>
    /// Per-connection bindings from an interned entity handle to the entity id it
    /// stands for, valid for one keyframe interval.
    /// </summary>
    /// <remarks>
    /// Handles are allocated from 1, reset at every keyframe, and never reused
    /// within an interval — so a handle we hold is either correct or unresolvable,
    /// never silently reassigned to a different entity.
    /// </remarks>
    public sealed class EntityHandleTable
    {
        private readonly Dictionary<uint, string> _bindings = new Dictionary<uint, string>();

        public int Count => _bindings.Count;

        /// <summary>
        /// Drops every binding. Called on each keyframe, because the sender restarts
        /// its handle space there: old bindings do not go stale, they become
        /// actively wrong.
        /// </summary>
        public void Clear() => _bindings.Clear();

        public void Bind(uint handle, string id)
        {
            if (handle == 0 || string.IsNullOrEmpty(id))
            {
                return;
            }

            _bindings[handle] = id;
        }

        public bool TryResolve(uint handle, out string id) => _bindings.TryGetValue(handle, out id);
    }
}
