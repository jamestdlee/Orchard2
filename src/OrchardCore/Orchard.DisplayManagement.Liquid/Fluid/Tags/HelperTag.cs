using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Fluid;
using Fluid.Ast;
using Fluid.Tags;
using Fluid.Values;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.DependencyInjection;
using Orchard.DisplayManagement.Fluid.Ast;
using Orchard.DisplayManagement.Fluid.Filters;
using Orchard.DisplayManagement.Liquid;

namespace Orchard.DisplayManagement.Fluid.Tags
{
    public class HelperTag : ArgumentsTag
    {
        public override Task<Completion> WriteToAsync(TextWriter writer, TextEncoder encoder, TemplateContext context, FilterArgument[] arguments)
        {
            return new HelperStatement(new ArgumentsExpression(arguments)).WriteToAsync(writer, encoder, context);
        }
    }

    public class HelperBlock : ArgumentsBlock
    {
        public override Task<Completion> WriteToAsync(TextWriter writer, TextEncoder encoder, TemplateContext context, FilterArgument[] arguments, IList<Statement> statements)
        {
            return new HelperStatement(new ArgumentsExpression(arguments), null, statements).WriteToAsync(writer, encoder, context);
        }
    }

    public class HelperStatement : TagStatement
    {
        private static ConcurrentDictionary<Type, Func<RazorPage, ITagHelper>> _tagHelperActivators = new ConcurrentDictionary<Type, Func<RazorPage, ITagHelper>>();
        private static ConcurrentDictionary<string, Action<ITagHelper, FluidValue>> _tagHelperSetters = new ConcurrentDictionary<string, Action<ITagHelper, FluidValue>>();
        private TagHelperDescriptor _descriptor;

        private readonly ArgumentsExpression _arguments;
        private readonly string _helper;

        public HelperStatement(ArgumentsExpression arguments, string helper = null, IList<Statement> statements = null) : base(statements)
        {
            _arguments = arguments;
            _helper = helper;
        }

        public override async Task<Completion> WriteToAsync(TextWriter writer, TextEncoder encoder, TemplateContext context)
        {
            if (!context.AmbientValues.TryGetValue("Services", out var servicesValue))
            {
                throw new ArgumentException("Services missing while invoking 'helper'");
            }

            var services = servicesValue as IServiceProvider;

            if (!context.AmbientValues.TryGetValue("ViewContext", out var viewContext))
            {
                throw new ArgumentException("ViewContext missing while invoking 'helper'");
            }

            var razorPage = (((ViewContext)viewContext).View as RazorView)?.RazorPage as RazorPage;

            if (razorPage == null)
            {
                return Completion.Normal;
            }

            var arguments = (FilterArguments)(await _arguments.EvaluateAsync(context)).ToObjectValue();

            var helper = _helper ?? arguments.At(0).ToStringValue();
            var tagHelperSharedState = services.GetRequiredService<TagHelperSharedState>();

            if (tagHelperSharedState.TagHelperDescriptors == null)
            {
                lock (tagHelperSharedState)
                {
                    if (tagHelperSharedState.TagHelperDescriptors == null)
                    {
                        var razorEngine = services.GetRequiredService<RazorEngine>();
                        var tagHelperFeature = razorEngine.Features.OfType<ITagHelperFeature>().FirstOrDefault();
                        tagHelperSharedState.TagHelperDescriptors = tagHelperFeature.GetDescriptors().ToList();
                    }
                }
            }

            if (_descriptor == null)
            {
                lock (this)
                {
                    var descriptors = tagHelperSharedState.TagHelperDescriptors
                        .Where(d => d.TagMatchingRules.OfType<TagMatchingRuleDescriptor>().Any(r =>
                            ((r.TagName == "*") || r.TagName == helper) && r.Attributes.All(a => arguments.Names.Any(n =>
                            {
                                if (String.Equals(n, a.Name, StringComparison.OrdinalIgnoreCase))
                                {
                                    return true;
                                }

                                n = n.Replace("_", "-");

                                if (a.Name.StartsWith("asp-") && String.Equals(n, a.Name.Substring(4), StringComparison.OrdinalIgnoreCase))
                                {
                                    return true;
                                }

                                if (String.Equals(n, a.Name, StringComparison.OrdinalIgnoreCase))
                                {
                                    return true;
                                }

                                return false;
                            }))));

                    _descriptor = descriptors.FirstOrDefault();

                    if (_descriptor == null)
                    {
                        return Completion.Normal;
                    }
                }
            }
            
            var tagHelperType = Type.GetType(_descriptor.Name + ", " + _descriptor.AssemblyName);

            var _tagHelperActivator = _tagHelperActivators.GetOrAdd(tagHelperType, key =>
            {
                var methodInfo = typeof(RazorPage).GetMethod("CreateTagHelper").MakeGenericMethod(key);
                return Delegate.CreateDelegate(typeof(Func<RazorPage, ITagHelper>), methodInfo) as Func<RazorPage, ITagHelper>;
            });

            var tagHelper = _tagHelperActivator(razorPage);

            var attributes = new TagHelperAttributeList();

            foreach (var name in arguments.Names)
            {
                var propertyName = FluidViewFilters.LowerKebabToPascalCase(name);
                var attributeName = name.Replace("_", "-");
                var found = false;

                foreach (var attribute in _descriptor.BoundAttributes)
                {
                    if (propertyName == attribute.GetPropertyName())
                    {
                        found = true;

                        var setter = _tagHelperSetters.GetOrAdd(attribute.DisplayName, key =>
                        {
                            var propertyInfo = tagHelperType.GetProperty(propertyName);
                            var propertySetter = propertyInfo.GetSetMethod();

                            var invokeType = typeof(Action<,>).MakeGenericType(tagHelperType, propertyInfo.PropertyType);
                            var setterDelegate = Delegate.CreateDelegate(invokeType, propertySetter);

                            Action<ITagHelper, FluidValue> result = (h, v) =>
                            {
                                object value = null;

                                if (attribute.IsEnum)
                                {
                                    value = Enum.Parse(propertyInfo.PropertyType, v.ToStringValue());
                                }
                                else if (attribute.IsStringProperty)
                                {
                                    value = v.ToStringValue();
                                }
                                else if (propertyInfo.PropertyType == typeof(Boolean))
                                {
                                    value = Convert.ToBoolean(v.ToStringValue());
                                }
                                // Todo: implement attribute.IsIndexer
                                else
                                {
                                    value = v.ToObjectValue();
                                }

                                setterDelegate.DynamicInvoke(new[] { h, value });
                            };

                            return result;
                        });

                        try
                        {
                            setter(tagHelper, arguments[name]);
                        }
                        catch (ArgumentException e)
                        {
                            throw new ArgumentException("Incorrect value type assigned to a tag.", name, e);
                        }

                        break;
                    }
                }

                if (!found)
                {
                    attributes.Add(new TagHelperAttribute(attributeName, arguments[name].ToObjectValue()));
                }
            }

            var content = new StringWriter();
            if (Statements?.Any() ?? false)
            {
                Completion completion = Completion.Break;
                for (var index = 0; index < Statements.Count; index++)
                {
                    completion = await Statements[index].WriteToAsync(content, encoder, context);

                    if (completion != Completion.Normal)
                    {
                        return completion;
                    }
                }
            }

            var tagHelperContext = new TagHelperContext(attributes,
                new Dictionary<object, object>(), Guid.NewGuid().ToString("N"));

            var tagHelperOutput = new TagHelperOutput(helper, attributes, (_, e)
                => Task.FromResult(new DefaultTagHelperContent().AppendHtml(content.ToString())));

            tagHelperOutput.Content.AppendHtml(content.ToString());
            await tagHelper.ProcessAsync(tagHelperContext, tagHelperOutput);

            tagHelperOutput.WriteTo(writer, HtmlEncoder.Default);

            return Completion.Normal;
        }
    }
}