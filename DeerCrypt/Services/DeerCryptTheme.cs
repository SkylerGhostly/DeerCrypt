using System.IO;
using System.Text;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Themes;

namespace DeerCrypt.Services
{
    /// <summary>
    /// Provides the DeerCrypt editor palette as embedded TextMate theme JSON
    /// (no loose files - both themes live as C# raw string literals).
    ///
    /// Design goals:
    ///  • Muted, cool-toned palette - nothing eye-blasting
    ///  • Every token stays readable on #1A1A1A (dark) or #F5F5F5 (light)
    ///  • Hues are distinct enough to be meaningful without being garish
    /// </summary>
    internal static class DeerCryptTheme
    {
        /// <summary>Parses the dark theme JSON; returns null if parsing fails.</summary>
        public static IRawTheme? TryLoadDark( )  => TryLoad( DarkJson  );

        /// <summary>Parses the light theme JSON; returns null if parsing fails.</summary>
        public static IRawTheme? TryLoadLight( ) => TryLoad( LightJson );

        private static IRawTheme? TryLoad( string json )
        {
            try
            {
                using var stream = new MemoryStream( Encoding.UTF8.GetBytes( json ) );
                using var reader = new StreamReader( stream );
                return ThemeReader.ReadThemeSync( reader );
            }
            catch { return null; }
        }

        // Dark theme 
        // Palette (hex, all on #1A1A1A background):
        //  Keywords       #B0A0D8  - soft lavender
        //  Strings        #D4AC70  - warm amber
        //  Comments       #6B8E6B  - muted sage (italic)
        //  Numbers        #CC8866  - muted terracotta
        //  Types/Classes  #7AAEC8  - soft steel-blue
        //  Functions      #84B4D4  - lighter steel-blue
        //  Operators      #9AAFBB  - blue-gray
        //  Preprocessor   #C08080  - muted rose
        //  Decorators     #A0C0A0  - muted mint
        //  HTML/XML tags  #84B4D4  - same as functions
        //  HTML attrs     #B0C0A0  - muted olive
        //  Default text   #D0D0D0

        public const string DarkJson = """
            {
              "name": "DeerCrypt Dark",
              "type": "dark",
              "colors": {
                "editor.background":              "#1A1A1A",
                "editor.foreground":              "#D0D0D0",
                "editor.lineHighlightBackground": "#18FFFFFF",
                "editorLineNumber.foreground":    "#555C6B",
                "editorCursor.foreground":        "#AEAEAE"
              },
              "tokenColors": [
                {
                  "name": "Comments",
                  "scope": [
                    "comment",
                    "comment.block",
                    "comment.line",
                    "punctuation.definition.comment"
                  ],
                  "settings": { "foreground": "#6B8E6B", "fontStyle": "italic" }
                },
                {
                  "name": "Documentation comments",
                  "scope": [
                    "comment.block.documentation",
                    "comment.line.documentation"
                  ],
                  "settings": { "foreground": "#7A9E7A", "fontStyle": "italic" }
                },
                {
                  "name": "Keywords & control flow",
                  "scope": [
                    "keyword",
                    "keyword.control",
                    "keyword.other",
                    "storage.type",
                    "storage.modifier",
                    "keyword.operator.new",
                    "keyword.operator.delete",
                    "keyword.operator.typeof",
                    "keyword.operator.instanceof",
                    "keyword.operator.logical.python"
                  ],
                  "settings": { "foreground": "#B0A0D8" }
                },
                {
                  "name": "Strings (single, double, template)",
                  "scope": [
                    "string",
                    "string.quoted",
                    "string.template",
                    "string.interpolated",
                    "string.other"
                  ],
                  "settings": { "foreground": "#D4AC70" }
                },
                {
                  "name": "String escape sequences",
                  "scope": "constant.character.escape",
                  "settings": { "foreground": "#CC9060" }
                },
                {
                  "name": "Regular expressions",
                  "scope": "string.regexp",
                  "settings": { "foreground": "#D0A880" }
                },
                {
                  "name": "Numeric & boolean literals",
                  "scope": [
                    "constant.numeric",
                    "constant.language.boolean",
                    "constant.language.null",
                    "constant.language.undefined",
                    "constant.language.none.python"
                  ],
                  "settings": { "foreground": "#CC8866" }
                },
                {
                  "name": "Language constants (this / self / super)",
                  "scope": [
                    "constant.language",
                    "variable.language.this",
                    "variable.language.self",
                    "variable.language.super"
                  ],
                  "settings": { "foreground": "#CC8866" }
                },
                {
                  "name": "Types, classes, interfaces, enums",
                  "scope": [
                    "entity.name.type",
                    "entity.name.class",
                    "entity.name.interface",
                    "entity.name.struct",
                    "entity.name.enum",
                    "entity.name.namespace",
                    "entity.name.module",
                    "support.type",
                    "support.class"
                  ],
                  "settings": { "foreground": "#7AAEC8" }
                },
                {
                  "name": "Generic / type parameters",
                  "scope": [
                    "entity.name.type.parameter",
                    "variable.type.parameter"
                  ],
                  "settings": { "foreground": "#8ABCD8" }
                },
                {
                  "name": "Functions & methods",
                  "scope": [
                    "entity.name.function",
                    "support.function",
                    "variable.function",
                    "meta.function-call entity.name.function"
                  ],
                  "settings": { "foreground": "#84B4D4" }
                },
                {
                  "name": "Function parameters",
                  "scope": "variable.parameter",
                  "settings": { "foreground": "#C0BBCC" }
                },
                {
                  "name": "Operators",
                  "scope": [
                    "keyword.operator",
                    "punctuation.accessor",
                    "punctuation.separator.key-value",
                    "keyword.operator.arrow"
                  ],
                  "settings": { "foreground": "#9AAFBB" }
                },
                {
                  "name": "Preprocessor, imports & using",
                  "scope": [
                    "meta.preprocessor",
                    "keyword.control.import",
                    "keyword.control.export",
                    "keyword.control.from",
                    "keyword.other.using",
                    "keyword.other.include",
                    "support.other.namespace"
                  ],
                  "settings": { "foreground": "#C08080" }
                },
                {
                  "name": "Decorators & attributes",
                  "scope": [
                    "meta.decorator",
                    "entity.name.function.decorator",
                    "punctuation.definition.annotation",
                    "meta.attribute"
                  ],
                  "settings": { "foreground": "#A0C0A0" }
                },
                {
                  "name": "HTML & XML tag names",
                  "scope": [
                    "entity.name.tag",
                    "meta.tag punctuation.definition.tag"
                  ],
                  "settings": { "foreground": "#84B4D4" }
                },
                {
                  "name": "HTML & XML attribute names",
                  "scope": "entity.other.attribute-name",
                  "settings": { "foreground": "#B0C0A0" }
                },
                {
                  "name": "HTML & XML attribute values",
                  "scope": [
                    "meta.attribute string",
                    "string.quoted.attribute-value"
                  ],
                  "settings": { "foreground": "#D4AC70" }
                },
                {
                  "name": "CSS property names",
                  "scope": [
                    "support.type.property-name.css",
                    "meta.property-name.css"
                  ],
                  "settings": { "foreground": "#B0C0A0" }
                },
                {
                  "name": "CSS values & units",
                  "scope": [
                    "support.constant.property-value.css",
                    "keyword.other.unit.css"
                  ],
                  "settings": { "foreground": "#D4AC70" }
                },
                {
                  "name": "CSS selectors",
                  "scope": [
                    "entity.name.tag.css",
                    "entity.other.attribute-name.class.css",
                    "entity.other.attribute-name.id.css"
                  ],
                  "settings": { "foreground": "#7AAEC8" }
                },
                {
                  "name": "JSON property keys",
                  "scope": "support.type.property-name.json",
                  "settings": { "foreground": "#84B4D4" }
                },
                {
                  "name": "YAML keys",
                  "scope": "entity.name.tag.yaml",
                  "settings": { "foreground": "#84B4D4" }
                },
                {
                  "name": "TOML keys",
                  "scope": "support.type.property-name.toml",
                  "settings": { "foreground": "#84B4D4" }
                },
                {
                  "name": "TOML section headings",
                  "scope": "entity.name.section.group-title.toml",
                  "settings": { "foreground": "#7AAEC8" }
                },
                {
                  "name": "Shell built-ins & commands",
                  "scope": [
                    "support.function.builtin.shell",
                    "entity.name.command.shell"
                  ],
                  "settings": { "foreground": "#84B4D4" }
                },
                {
                  "name": "Markdown headings",
                  "scope": [
                    "markup.heading",
                    "entity.name.section.markdown",
                    "punctuation.definition.heading.markdown"
                  ],
                  "settings": { "foreground": "#7AAEC8", "fontStyle": "bold" }
                },
                {
                  "name": "Markdown bold",
                  "scope": "markup.bold",
                  "settings": { "foreground": "#D0D0D0", "fontStyle": "bold" }
                },
                {
                  "name": "Markdown italic",
                  "scope": "markup.italic",
                  "settings": { "foreground": "#D0D0D0", "fontStyle": "italic" }
                },
                {
                  "name": "Markdown inline code & fences",
                  "scope": [
                    "markup.inline.raw",
                    "markup.fenced_code.block"
                  ],
                  "settings": { "foreground": "#D4AC70" }
                },
                {
                  "name": "Markdown links",
                  "scope": [
                    "markup.underline.link",
                    "string.other.link.title.markdown"
                  ],
                  "settings": { "foreground": "#84B4D4" }
                },
                {
                  "name": "Invalid & deprecated",
                  "scope": [
                    "invalid",
                    "invalid.deprecated"
                  ],
                  "settings": { "foreground": "#C08080" }
                }
              ]
            }
            """;

        // Light theme 
        // Same semantic roles, re-tuned for a light (#F5F5F5) background:
        //  Keywords      #7060B0  - muted purple
        //  Strings       #9A6020  - warm brown
        //  Comments      #4A7A4A  - muted green (italic)
        //  Numbers       #A04020  - muted rust
        //  Types         #2060A0  - muted blue
        //  Functions     #2070A0  - slightly lighter blue
        //  Operators     #506070  - dark slate
        //  Preprocessor  #904040  - muted dark-rose
        //  Decorators    #406040  - muted dark-olive
        //  Default text  #282828

        public const string LightJson = """
            {
              "name": "DeerCrypt Light",
              "type": "light",
              "colors": {
                "editor.background":              "#F5F5F5",
                "editor.foreground":              "#282828",
                "editor.lineHighlightBackground": "#10000000",
                "editorLineNumber.foreground":    "#888898",
                "editorCursor.foreground":        "#444444"
              },
              "tokenColors": [
                {
                  "name": "Comments",
                  "scope": [
                    "comment",
                    "comment.block",
                    "comment.line",
                    "punctuation.definition.comment"
                  ],
                  "settings": { "foreground": "#4A7A4A", "fontStyle": "italic" }
                },
                {
                  "name": "Keywords & control flow",
                  "scope": [
                    "keyword",
                    "keyword.control",
                    "keyword.other",
                    "storage.type",
                    "storage.modifier",
                    "keyword.operator.new",
                    "keyword.operator.delete",
                    "keyword.operator.typeof",
                    "keyword.operator.instanceof"
                  ],
                  "settings": { "foreground": "#7060B0" }
                },
                {
                  "name": "Strings",
                  "scope": [
                    "string",
                    "string.quoted",
                    "string.template",
                    "string.interpolated"
                  ],
                  "settings": { "foreground": "#9A6020" }
                },
                {
                  "name": "String escapes",
                  "scope": "constant.character.escape",
                  "settings": { "foreground": "#A05030" }
                },
                {
                  "name": "Regular expressions",
                  "scope": "string.regexp",
                  "settings": { "foreground": "#A06030" }
                },
                {
                  "name": "Numeric & boolean literals",
                  "scope": [
                    "constant.numeric",
                    "constant.language.boolean",
                    "constant.language.null",
                    "constant.language.undefined"
                  ],
                  "settings": { "foreground": "#A04020" }
                },
                {
                  "name": "Language constants",
                  "scope": [
                    "constant.language",
                    "variable.language.this",
                    "variable.language.self",
                    "variable.language.super"
                  ],
                  "settings": { "foreground": "#A04020" }
                },
                {
                  "name": "Types, classes, interfaces",
                  "scope": [
                    "entity.name.type",
                    "entity.name.class",
                    "entity.name.interface",
                    "entity.name.struct",
                    "entity.name.enum",
                    "entity.name.namespace",
                    "support.type",
                    "support.class"
                  ],
                  "settings": { "foreground": "#2060A0" }
                },
                {
                  "name": "Functions & methods",
                  "scope": [
                    "entity.name.function",
                    "support.function",
                    "variable.function"
                  ],
                  "settings": { "foreground": "#2070A0" }
                },
                {
                  "name": "Function parameters",
                  "scope": "variable.parameter",
                  "settings": { "foreground": "#383848" }
                },
                {
                  "name": "Operators",
                  "scope": [
                    "keyword.operator",
                    "punctuation.accessor"
                  ],
                  "settings": { "foreground": "#506070" }
                },
                {
                  "name": "Preprocessor & imports",
                  "scope": [
                    "meta.preprocessor",
                    "keyword.control.import",
                    "keyword.control.export",
                    "keyword.other.using",
                    "keyword.other.include"
                  ],
                  "settings": { "foreground": "#904040" }
                },
                {
                  "name": "Decorators & attributes",
                  "scope": [
                    "meta.decorator",
                    "entity.name.function.decorator"
                  ],
                  "settings": { "foreground": "#406040" }
                },
                {
                  "name": "HTML & XML tag names",
                  "scope": "entity.name.tag",
                  "settings": { "foreground": "#2060A0" }
                },
                {
                  "name": "HTML & XML attribute names",
                  "scope": "entity.other.attribute-name",
                  "settings": { "foreground": "#406040" }
                },
                {
                  "name": "CSS property names",
                  "scope": "support.type.property-name.css",
                  "settings": { "foreground": "#406040" }
                },
                {
                  "name": "CSS values",
                  "scope": "support.constant.property-value.css",
                  "settings": { "foreground": "#9A6020" }
                },
                {
                  "name": "CSS selectors",
                  "scope": [
                    "entity.name.tag.css",
                    "entity.other.attribute-name.class.css",
                    "entity.other.attribute-name.id.css"
                  ],
                  "settings": { "foreground": "#2060A0" }
                },
                {
                  "name": "JSON property keys",
                  "scope": "support.type.property-name.json",
                  "settings": { "foreground": "#2070A0" }
                },
                {
                  "name": "YAML / TOML keys",
                  "scope": [
                    "entity.name.tag.yaml",
                    "support.type.property-name.toml"
                  ],
                  "settings": { "foreground": "#2070A0" }
                },
                {
                  "name": "Markdown headings",
                  "scope": "markup.heading",
                  "settings": { "foreground": "#2060A0", "fontStyle": "bold" }
                },
                {
                  "name": "Markdown code",
                  "scope": "markup.inline.raw",
                  "settings": { "foreground": "#9A6020" }
                }
              ]
            }
            """;
    }
}
