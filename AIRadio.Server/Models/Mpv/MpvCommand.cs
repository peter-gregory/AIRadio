using System.Text.Json.Serialization;

namespace AIRadio.Server.Models.Mpv
{
    public sealed class MpvCommand
    {
        public MpvCommand(
            string name,
            params object?[] arguments)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            Name = name;
            Arguments = arguments ?? [];
        }

        public long? RequestId { get; set; }

        public string Name { get; }

        public IReadOnlyList<object?> Arguments { get; }

        public object[] ToCommandArray()
        {
            var result =
                new object[Arguments.Count + 1];

            result[0] = Name;

            for (var i = 0; i < Arguments.Count; i++)
            {
                result[i + 1] =
                    Arguments[i]!;
            }

            return result;
        }

        public static MpvCommand LoadFile(
            string url,
            string mode = "replace")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(url);

            return new MpvCommand(
                "loadfile",
                url,
                mode);
        }

        public static MpvCommand Stop()
        {
            return new MpvCommand(
                "stop");
        }

        public static MpvCommand Pause()
        {
            return SetProperty(
                "pause",
                true);
        }

        public static MpvCommand Resume()
        {
            return SetProperty(
                "pause",
                false);
        }

        public static MpvCommand SetProperty(
            string property,
            object? value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                property);

            return new MpvCommand(
                "set_property",
                property,
                value);
        }

        public static MpvCommand GetProperty(
            string property)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                property);

            return new MpvCommand(
                "get_property",
                property);
        }

        public static MpvCommand ObserveProperty(
            int propertyId,
            string property)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                property);

            return new MpvCommand(
                "observe_property",
                propertyId,
                property);
        }

        public static MpvCommand UnobserveProperty(
            int propertyId)
        {
            return new MpvCommand(
                "unobserve_property",
                propertyId);
        }

        public static MpvCommand SetPropertyString(
            string property,
            string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                property);

            return new MpvCommand(
                "set_property",
                property,
                value);
        }

        public static MpvCommand SetPropertyBoolean(
            string property,
            bool value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                property);

            return new MpvCommand(
                "set_property",
                property,
                value);
        }

        public static MpvCommand SetPropertyNumber(
            string property,
            double value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                property);

            return new MpvCommand(
                "set_property",
                property,
                value);
        }

        public static MpvCommand Command(
            string name,
            params object?[] arguments)
        {
            return new MpvCommand(
                name,
                arguments);
        }
    }
}
