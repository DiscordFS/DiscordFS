using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace DiscordFS.Helpers;

public class DateTimeOffsetConverter : IYamlTypeConverter
{
    public bool Accepts(Type type)
    {
        return type == typeof(DateTimeOffset);
    }

    public object ReadYaml(IParser parser, Type type)
    {
        var value = ((Scalar)parser.Current).Value;

        var dto = DateTimeOffset.Parse(value);

        parser.MoveNext();
        return dto;
    }

    public void WriteYaml(IEmitter emitter, object value, Type type)
    {
        var dto = (DateTimeOffset)value;

        var formatted = dto.ToString(format: "O");
        emitter.Emit(new Scalar(anchor: null, tag: null, formatted, ScalarStyle.Any, isPlainImplicit: true, isQuotedImplicit: false));
    }
}