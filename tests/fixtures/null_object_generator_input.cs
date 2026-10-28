using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CivicSurvival.Core.Attributes;

namespace NullFixtures
{
    public enum Policy { Active, Unavailable }

    [GenerateNullObject]
    public interface IService
    {
        [NullReturn(true)] bool Enabled { get; set; }
        [NullReturn(Policy.Unavailable)] Policy CurrentPolicy { get; }
        [NullReturn("none")] string Label { get; }
        [NullReturnNull] object? State { get; }
        [NullReturnEmpty] IReadOnlyCollection<int> Items { get; }
        [NullReturnEmpty] IReadOnlyList<string> Names();
        [NullReturnCompletedDefault] Task<int> Pending();
        void /* trailing trivia */ Notify();
    }

    [GenerateNullObject]
    public interface IValue<T> where T : struct, IComparable<T>
    {
        T Read();
    }

    [GenerateNullObject]
    public interface IReference<T> where T : class, new()
    {
        [NullReturnNull] T? Read();
    }

    public sealed class Payload { }

    public static class Checks
    {
        private static T InferService<T>(T fallback)
        {
            if (!typeof(T).IsInterface)
                throw new Exception("Fallback must infer the service interface");
            return fallback;
        }

        public static void Run()
        {
            IService service = NullService.Instance;
            if (!ReferenceEquals(service, InferService(NullService.Instance)))
                throw new Exception("Singleton identity changed");
            service.Notify();
            service.Enabled = false;
            if (!service.Enabled || service.CurrentPolicy != Policy.Unavailable
                || service.Label != "none" || service.State != null
                || service.Items.Count != 0 || service.Names().Count != 0
                || !service.Pending().IsCompleted || service.Pending().Result != 0)
                throw new Exception("Declared null fallback semantics changed");
            if (NullValue<int>.Instance.Read() != 0 || NullReference<Payload>.Instance.Read() != null)
                throw new Exception("Generic constraint or fallback semantics changed");
        }
    }
}
