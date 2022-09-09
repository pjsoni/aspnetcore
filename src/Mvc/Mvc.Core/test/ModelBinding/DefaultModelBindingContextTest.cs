// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Internal;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Microsoft.AspNetCore.Mvc.ModelBinding
{
    public class DefaultModelBindingContextTest
    {
        [Fact]
        public void EnterNestedScope_CopiesProperties()
        {
            // Arrange
            var bindingContext = new DefaultModelBindingContext
            {
                Model = new object(),
                ModelMetadata = new TestModelMetadataProvider().GetMetadataForType(typeof(object)),
                ModelName = "theName",
                ValueProvider = new SimpleValueProvider(),
                ModelState = new ModelStateDictionary(),
            };

            var metadataProvider = new TestModelMetadataProvider();
            metadataProvider.ForType<object>().BindingDetails(d =>
            {
                d.BindingSource = BindingSource.Custom;
                d.BinderType = typeof(TestModelBinder);
                d.BinderModelName = "custom";
            });

            var newModelMetadata = metadataProvider.GetMetadataForType(typeof(object));

            // Act
            var originalBinderModelName = bindingContext.BinderModelName;
            var originalBindingSource = bindingContext.BindingSource;
            var originalValueProvider = bindingContext.ValueProvider;

            var disposable = bindingContext.EnterNestedScope(
                modelMetadata: newModelMetadata,
                fieldName: "fieldName",
                modelName: "modelprefix.fieldName",
                model: null);

            // Assert
            Assert.Same(newModelMetadata.BinderModelName, bindingContext.BinderModelName);
            Assert.Same(newModelMetadata.BindingSource, bindingContext.BindingSource);
            Assert.Equal("fieldName", bindingContext.FieldName);
            Assert.False(bindingContext.IsTopLevelObject);
            Assert.Null(bindingContext.Model);
            Assert.Same(newModelMetadata, bindingContext.ModelMetadata);
            Assert.Equal("modelprefix.fieldName", bindingContext.ModelName);
            Assert.Same(originalValueProvider, bindingContext.ValueProvider);

            disposable.Dispose();
        }

        [Fact]
        public void CreateBindingContext_FiltersValueProviders_ForValueProviderSource()
        {
            // Arrange
            var metadataProvider = new TestModelMetadataProvider();

            var original = CreateDefaultValueProvider();

            // Act
            var context = DefaultModelBindingContext.CreateBindingContext(
                GetActionContext(),
                original,
                metadataProvider.GetMetadataForType(typeof(object)),
                new BindingInfo() { BindingSource = BindingSource.Query },
                "model");

            // Assert
            Assert.Collection(
                Assert.IsType<CompositeValueProvider>(context.ValueProvider),
                vp => Assert.Same(original[1], vp));
        }

        [Fact]
        public void EnterNestedScope_FiltersValueProviders_ForValueProviderSource()
        {
            // Arrange
            var metadataProvider = new TestModelMetadataProvider();
            metadataProvider
                .ForProperty(typeof(string), nameof(string.Length))
                .BindingDetails(b => b.BindingSource = BindingSource.Query);

            var original = CreateDefaultValueProvider();
            var context = DefaultModelBindingContext.CreateBindingContext(
                GetActionContext(),
                original,
                metadataProvider.GetMetadataForType(typeof(string)),
                new BindingInfo(),
                "model");

            var propertyMetadata = metadataProvider.GetMetadataForProperty(typeof(string), nameof(string.Length));

            // Act
            context.EnterNestedScope(propertyMetadata, "Length", "Length", model: null);

            // Assert
            Assert.Collection(
                Assert.IsType<CompositeValueProvider>(context.ValueProvider),
                vp => Assert.Same(original[1], vp));
        }

        [Fact]
        public void EnterNestedScope_FiltersValueProviders_BasedOnTopLevelValueProviders()
        {
            // Arrange
            var metadataProvider = new TestModelMetadataProvider();
            metadataProvider
                .ForProperty(typeof(string), nameof(string.Length))
                .BindingDetails(b => b.BindingSource = BindingSource.Form);

            var original = CreateDefaultValueProvider();

            var context = DefaultModelBindingContext.CreateBindingContext(
                GetActionContext(),
                original,
                metadataProvider.GetMetadataForType(typeof(string)),
                new BindingInfo() { BindingSource = BindingSource.Query },
                "model");

            var propertyMetadata = metadataProvider.GetMetadataForProperty(typeof(string), nameof(string.Length));

            // Act
            context.EnterNestedScope(propertyMetadata, "Length", "Length", model: null);

            // Assert
            Assert.Collection(
                Assert.IsType<CompositeValueProvider>(context.ValueProvider),
                vp => Assert.Same(original[2], vp));
        }

        // Ensure model binding system disallows one more than MaxModelBindingRecursionDepth binders on the stack.
        [Fact]
        public void EnterNestedScope_Throws_WhenDeeperThanMaxModelBindingRecursionDepth()
        {
            // Arrange
            var expectedMessage = $"Model binding system exceeded " +
               $"MaxModelBindingRecursionDepth (3). Reduce the " +
               $"potential nesting of '{typeof(string)}'. For example, this type may have a property with a " +
               $"model binder that always succeeds. " +
               $"Otherwise, consider setting the AppContext switch 'Microsoft.AspNetCore.Mvc.ModelBinding.MaxRecursionDepth' to change the default depth.";

            var metadataProvider = new TestModelMetadataProvider();
            metadataProvider
                .ForProperty(typeof(string), nameof(string.Length))
                .BindingDetails(b => b.BindingSource = BindingSource.Query);

            var original = CreateDefaultValueProvider();
            var context = DefaultModelBindingContext.CreateBindingContext(
                GetActionContext(),
                original,
                metadataProvider.GetMetadataForType(typeof(string)),
                new BindingInfo(),
                "model") as DefaultModelBindingContext;
            context.MaxModelBindingRecursionDepth = 3;

            var propertyMetadata = metadataProvider.GetMetadataForProperty(typeof(string), nameof(string.Length));

            void RecursiveNestedScope(int depth = 0)
            {
                if (depth >= context.MaxModelBindingRecursionDepth)
                {
                    return;
                }

                using (context.EnterNestedScope(propertyMetadata, "Length", "Length", model: null))
                {
                    RecursiveNestedScope(++depth);
                }
            }

            // Act & Assert
            var exception = Assert.Throws<InvalidOperationException>(
                () => RecursiveNestedScope());
            Assert.Equal(expectedMessage, exception.Message);            
        }


        [Fact]
        public void ModelTypeAreFedFromModelMetadata()
        {
            // Act
            var bindingContext = new DefaultModelBindingContext
            {
                ModelMetadata = new EmptyModelMetadataProvider().GetMetadataForType(typeof(int))
            };

            // Assert
            Assert.Equal(typeof(int), bindingContext.ModelType);
        }

        private static ActionContext GetActionContext()
        {
            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

            return new ActionContext()
            {
                HttpContext = new DefaultHttpContext()
                {
                    RequestServices = services.BuildServiceProvider()
                }
            };
        }

        private static CompositeValueProvider CreateDefaultValueProvider()
        {
            var result = new CompositeValueProvider();
            result.Add(new RouteValueProvider(BindingSource.Path, new RouteValueDictionary()));
            result.Add(new QueryStringValueProvider(
                BindingSource.Query,
                new QueryCollection(),
                CultureInfo.InvariantCulture));
            result.Add(new FormValueProvider(
                BindingSource.Form,
                new FormCollection(new Dictionary<string, StringValues>()),
                CultureInfo.CurrentCulture));
            return result;
        }

        private class TestModelBinder : IModelBinder
        {
            public Task BindModelAsync(ModelBindingContext bindingContext)
            {
                if (bindingContext == null)
                {
                    throw new ArgumentNullException(nameof(bindingContext));
                }
                Debug.Assert(bindingContext.Result == null);

                throw new NotImplementedException();
            }
        }
    }
}
