﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using System.Collections.Generic;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Microsoft.EntityFrameworkCore.Query.Internal;

/// <summary>
///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
///     the same compatibility standards as public APIs. It may be changed or removed without notice in
///     any release. You should only use it directly in your code with extreme caution and knowing that
///     doing so can result in application failures when updating to a new Entity Framework Core release.
/// </summary>
public class ParameterExtractingExpressionVisitor : ExpressionVisitor
{
    private const string QueryFilterPrefix = "ef_filter";

    private readonly IParameterValues _parameterValues;
    private readonly IDiagnosticsLogger<DbLoggerCategory.Query> _logger;
    private readonly bool _parameterize;
    private readonly bool _generateContextAccessors;
    private readonly EvaluatableExpressionFindingExpressionVisitor _evaluatableExpressionFindingExpressionVisitor;
    private readonly ContextParameterReplacingExpressionVisitor _contextParameterReplacingExpressionVisitor;

    private readonly Dictionary<Expression, EvaluatedValues> _evaluatedValues = new(ExpressionEqualityComparer.Instance);

    private IDictionary<Expression, bool> _evaluatableExpressions;
    private IQueryProvider? _currentQueryProvider;

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    public ParameterExtractingExpressionVisitor(
        IEvaluatableExpressionFilter evaluatableExpressionFilter,
        IParameterValues parameterValues,
        Type contextType,
        IModel model,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger,
        bool parameterize,
        bool generateContextAccessors)
    {
        _evaluatableExpressionFindingExpressionVisitor
            = new EvaluatableExpressionFindingExpressionVisitor(evaluatableExpressionFilter, model, parameterize);
        _parameterValues = parameterValues;
        _logger = logger;
        _parameterize = parameterize;
        _generateContextAccessors = generateContextAccessors;
        // The entry method will take care of populating this field always. So accesses should be safe.
        _evaluatableExpressions = null!;
        _contextParameterReplacingExpressionVisitor = _generateContextAccessors
            ? new ContextParameterReplacingExpressionVisitor(contextType)
            : null!;
    }

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    public virtual Expression ExtractParameters(Expression expression)
        => ExtractParameters(expression, clearEvaluatedValues: true);

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    public virtual Expression ExtractParameters(Expression expression, bool clearEvaluatedValues)
    {
        var oldEvaluatableExpressions = _evaluatableExpressions;
        _evaluatableExpressions = _evaluatableExpressionFindingExpressionVisitor.Find(expression);

        try
        {
            return Visit(expression);
        }
        finally
        {
            _evaluatableExpressions = oldEvaluatableExpressions;
            if (clearEvaluatedValues)
            {
                _evaluatedValues.Clear();
            }
        }
    }

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    [return: NotNullIfNotNull("expression")]
    public override Expression? Visit(Expression? expression)
    {
        if (expression == null)
        {
            return null;
        }

        if (_evaluatableExpressions.TryGetValue(expression, out var generateParameter)
            && !PreserveInitializationConstant(expression, generateParameter)
            && !PreserveConvertNode(expression))
        {
            return Evaluate(expression, _parameterize && generateParameter);
        }

        return base.Visit(expression);
    }

    private static bool PreserveInitializationConstant(Expression expression, bool generateParameter)
        => !generateParameter && expression is NewExpression or MemberInitExpression;

    private bool PreserveConvertNode(Expression expression)
    {
        if (expression is UnaryExpression unaryExpression
            && (unaryExpression.NodeType == ExpressionType.Convert
                || unaryExpression.NodeType == ExpressionType.ConvertChecked))
        {
            if (unaryExpression.Type == typeof(object)
                || unaryExpression.Type == typeof(Enum)
                || unaryExpression.Operand.Type.UnwrapNullableType().IsEnum)
            {
                return true;
            }

            var innerType = unaryExpression.Operand.Type.UnwrapNullableType();
            if (unaryExpression.Type.UnwrapNullableType() == typeof(int)
                && (innerType == typeof(byte)
                    || innerType == typeof(sbyte)
                    || innerType == typeof(char)
                    || innerType == typeof(short)
                    || innerType == typeof(ushort)))
            {
                return true;
            }

            return PreserveConvertNode(unaryExpression.Operand);
        }

        return false;
    }

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    protected override Expression VisitConditional(ConditionalExpression conditionalExpression)
    {
        var newTestExpression = TryGetConstantValue(conditionalExpression.Test) ?? Visit(conditionalExpression.Test);

        if (newTestExpression is ConstantExpression { Value: bool constantTestValue })
        {
            return constantTestValue
                ? Visit(conditionalExpression.IfTrue)
                : Visit(conditionalExpression.IfFalse);
        }

        return conditionalExpression.Update(
            newTestExpression,
            Visit(conditionalExpression.IfTrue),
            Visit(conditionalExpression.IfFalse));
    }

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        // If this is a call to EF.Constant(), or EF.Parameter(), then examine the operand; it it's isn't evaluatable (i.e. contains a
        // reference to a database table), throw immediately. Otherwise, evaluate the operand (either as a constant or as a parameter) and
        // return that.
        if (methodCallExpression.Method.DeclaringType == typeof(EF))
        {
            switch (methodCallExpression.Method.Name)
            {
                case nameof(EF.Constant):
                    {
                        var operand = methodCallExpression.Arguments[0];
                        if (!_evaluatableExpressions.TryGetValue(operand, out _))
                        {
                            throw new InvalidOperationException("The EF.Constant<> method may only be used with an argument that can be evaluated client-side and does not contain any reference to database-side entities.");
                        }

                        return Evaluate(operand, generateParameter: false);
                    }

                case nameof(EF.Parameter):
                    {
                        var operand = methodCallExpression.Arguments[0];
                        if (!_evaluatableExpressions.TryGetValue(operand, out _))
                        {
                            throw new InvalidOperationException("The EF.Constant<> method may only be used with an argument that can be evaluated client-side and does not contain any reference to database-side entities.");
                        }

                        return Evaluate(operand, generateParameter: true);
                    }
            }
        }

        return base.VisitMethodCall(methodCallExpression);
    }

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    protected override Expression VisitBinary(BinaryExpression binaryExpression)
    {
        switch (binaryExpression.NodeType)
        {
            case ExpressionType.Coalesce:
                {
                    var newLeftExpression = TryGetConstantValue(binaryExpression.Left) ?? Visit(binaryExpression.Left);
                    if (newLeftExpression is ConstantExpression constantLeftExpression)
                    {
                        return constantLeftExpression.Value == null
                            ? Visit(binaryExpression.Right)
                            : newLeftExpression;
                    }

                    return binaryExpression.Update(
                        newLeftExpression,
                        binaryExpression.Conversion,
                        Visit(binaryExpression.Right));
                }

            case ExpressionType.AndAlso:
            case ExpressionType.OrElse:
                {
                    var newLeftExpression = TryGetConstantValue(binaryExpression.Left) ?? Visit(binaryExpression.Left);
                    if (ShortCircuitLogicalExpression(newLeftExpression, binaryExpression.NodeType))
                    {
                        return newLeftExpression;
                    }

                    var newRightExpression = TryGetConstantValue(binaryExpression.Right) ?? Visit(binaryExpression.Right);
                    return ShortCircuitLogicalExpression(newRightExpression, binaryExpression.NodeType)
                        ? newRightExpression
                        : binaryExpression.Update(newLeftExpression, binaryExpression.Conversion, newRightExpression);
                }

            default:
                return base.VisitBinary(binaryExpression);
        }
    }

    private Expression? TryGetConstantValue(Expression expression)
    {
        if (_evaluatableExpressions.ContainsKey(expression))
        {
            var value = GetValue(expression, out _);

            if (value is bool)
            {
                return Expression.Constant(value, typeof(bool));
            }
        }

        return null;
    }

    private static bool ShortCircuitLogicalExpression(Expression expression, ExpressionType nodeType)
        => expression is ConstantExpression { Value: bool constantValue }
            && ((constantValue && nodeType == ExpressionType.OrElse)
                || (!constantValue && nodeType == ExpressionType.AndAlso));

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    protected override Expression VisitExtension(Expression extensionExpression)
    {
        if (extensionExpression is QueryRootExpression queryRootExpression)
        {
            var queryProvider = queryRootExpression.QueryProvider;
            if (_currentQueryProvider == null)
            {
                _currentQueryProvider = queryProvider;
            }
            else if (!ReferenceEquals(queryProvider, _currentQueryProvider))
            {
                throw new InvalidOperationException(CoreStrings.ErrorInvalidQueryable);
            }

            // Visit after detaching query provider since custom query roots can have additional components
            extensionExpression = queryRootExpression.DetachQueryProvider();
        }

        return base.VisitExtension(extensionExpression);
    }

    private static Expression GenerateConstantExpression(object? value, Type returnType)
    {
        var constantExpression = Expression.Constant(value, value?.GetType() ?? returnType);

        return constantExpression.Type != returnType
            ? Expression.Convert(constantExpression, returnType)
            : constantExpression;
    }

    private Expression Evaluate(Expression expression, bool generateParameter)
    {
        object? parameterValue;
        string? parameterName;
        if (_evaluatedValues.TryGetValue(expression, out var cachedValue))
        {
            // The _generateContextAccessors condition allows us to reuse parameter expressions evaluated in query filters.
            // In principle, _generateContextAccessors is orthogonal to query filters, but in practice it is only used in the
            // nav expansion query filters (and defining query). If this changes in future, they would need to be decoupled.
            var existingExpression = generateParameter || _generateContextAccessors
                ? cachedValue.Parameter
                : cachedValue.Constant;

            if (existingExpression != null)
            {
                return existingExpression;
            }

            parameterValue = cachedValue.Value;
            parameterName = cachedValue.CandidateParameterName;
        }
        else
        {
            parameterValue = GetValue(expression, out parameterName);
            cachedValue = new EvaluatedValues { CandidateParameterName = parameterName, Value = parameterValue };
            _evaluatedValues[expression] = cachedValue;
        }

        if (parameterValue is IQueryable innerQueryable)
        {
            return ExtractParameters(innerQueryable.Expression, clearEvaluatedValues: false);
        }

        if (parameterName?.StartsWith(QueryFilterPrefix, StringComparison.Ordinal) != true)
        {
            if (parameterValue is Expression innerExpression)
            {
                return ExtractParameters(innerExpression, clearEvaluatedValues: false);
            }

            if (!generateParameter)
            {
                var constantValue = GenerateConstantExpression(parameterValue, expression.Type);

                cachedValue.Constant = constantValue;

                return constantValue;
            }
        }

        parameterName ??= "p";

        if (string.Equals(QueryFilterPrefix, parameterName, StringComparison.Ordinal))
        {
            parameterName = QueryFilterPrefix + "__p";
        }

        var compilerPrefixIndex
            = parameterName.LastIndexOf(">", StringComparison.Ordinal);

        if (compilerPrefixIndex != -1)
        {
            parameterName = parameterName[(compilerPrefixIndex + 1)..];
        }

        parameterName
            = QueryCompilationContext.QueryParameterPrefix
            + parameterName
            + "_"
            + _parameterValues.ParameterValues.Count;

        _parameterValues.AddParameter(parameterName, parameterValue);

        var parameter = Expression.Parameter(expression.Type, parameterName);

        cachedValue.Parameter = parameter;

        return parameter;
    }

    private sealed class ContextParameterReplacingExpressionVisitor : ExpressionVisitor
    {
        private readonly Type _contextType;

        public ContextParameterReplacingExpressionVisitor(Type contextType)
        {
            ContextParameterExpression = Expression.Parameter(contextType, "context");
            _contextType = contextType;
        }

        public ParameterExpression ContextParameterExpression { get; }

        [return: NotNullIfNotNull("expression")]
        public override Expression? Visit(Expression? expression)
            => expression?.Type != typeof(object)
                && expression?.Type.IsAssignableFrom(_contextType) == true
                    ? ContextParameterExpression
                    : base.Visit(expression);
    }

    private static Expression RemoveConvert(Expression expression)
    {
        if (expression is UnaryExpression unaryExpression
            && expression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            return RemoveConvert(unaryExpression.Operand);
        }

        return expression;
    }

    private object? GetValue(Expression? expression, out string? parameterName)
    {
        parameterName = null;

        if (expression == null)
        {
            return null;
        }

        if (_generateContextAccessors)
        {
            var newExpression = _contextParameterReplacingExpressionVisitor.Visit(expression);

            if (newExpression != expression)
            {
                if (newExpression.Type is IQueryable)
                {
                    return newExpression;
                }

                parameterName = QueryFilterPrefix
                    + (RemoveConvert(expression) is MemberExpression memberExpression
                        ? ("__" + memberExpression.Member.Name)
                        : "");

                return Expression.Lambda(
                    newExpression,
                    _contextParameterReplacingExpressionVisitor.ContextParameterExpression);
            }
        }

        switch (expression)
        {
            case MemberExpression memberExpression:
                var instanceValue = GetValue(memberExpression.Expression, out parameterName);
                try
                {
                    switch (memberExpression.Member)
                    {
                        case FieldInfo fieldInfo:
                            parameterName = (parameterName != null ? parameterName + "_" : "") + fieldInfo.Name;
                            return fieldInfo.GetValue(instanceValue);

                        case PropertyInfo propertyInfo:
                            parameterName = (parameterName != null ? parameterName + "_" : "") + propertyInfo.Name;
                            return propertyInfo.GetValue(instanceValue);
                    }
                }
                catch
                {
                    // Try again when we compile the delegate
                }

                break;

            case ConstantExpression constantExpression:
                return constantExpression.Value;

            case MethodCallExpression methodCallExpression:
                parameterName = methodCallExpression.Method.Name;
                break;

            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unaryExpression
                when (unaryExpression.Type.UnwrapNullableType() == unaryExpression.Operand.Type):
                return GetValue(unaryExpression.Operand, out parameterName);
        }

        try
        {
            return Expression.Lambda<Func<object>>(
                    Expression.Convert(expression, typeof(object)))
                .Compile(preferInterpretation: true)
                .Invoke();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                _logger.ShouldLogSensitiveData()
                    ? CoreStrings.ExpressionParameterizationExceptionSensitive(expression)
                    : CoreStrings.ExpressionParameterizationException,
                exception);
        }
    }

    private sealed class EvaluatableExpressionFindingExpressionVisitor : ExpressionVisitor
    {
        private readonly IEvaluatableExpressionFilter _evaluatableExpressionFilter;
        private readonly ISet<ParameterExpression> _allowedParameters = new HashSet<ParameterExpression>();
        private readonly IModel _model;
        private readonly bool _parameterize;

        private bool _evaluatable;
        private bool _containsClosure;
        private bool _inLambda;
        private IDictionary<Expression, bool> _evaluatableExpressions;

        public EvaluatableExpressionFindingExpressionVisitor(
            IEvaluatableExpressionFilter evaluatableExpressionFilter,
            IModel model,
            bool parameterize)
        {
            _evaluatableExpressionFilter = evaluatableExpressionFilter;
            _model = model;
            _parameterize = parameterize;
            // The entry method will take care of populating this field always. So accesses should be safe.
            _evaluatableExpressions = null!;
        }

        public IDictionary<Expression, bool> Find(Expression expression)
        {
            _evaluatable = true;
            _containsClosure = false;
            _inLambda = false;
            _evaluatableExpressions = new Dictionary<Expression, bool>();
            _allowedParameters.Clear();

            Visit(expression);

            return _evaluatableExpressions;
        }

        [return: NotNullIfNotNull("expression")]
        public override Expression? Visit(Expression? expression)
        {
            if (expression == null)
            {
                return base.Visit(expression);
            }

            var parentEvaluatable = _evaluatable;
            var parentContainsClosure = _containsClosure;

            _evaluatable = IsEvaluatableNodeType(expression, out var preferNoEvaluation)
                // Extension point to disable funcletization
                && _evaluatableExpressionFilter.IsEvaluatableExpression(expression, _model)
                // Don't evaluate QueryableMethods if in compiled query
                && (_parameterize || !IsQueryableMethod(expression));
            _containsClosure = false;

            base.Visit(expression);

            if (_evaluatable && !preferNoEvaluation)
            {
                // Force parameterization when not in lambda
                _evaluatableExpressions[expression] = _containsClosure || !_inLambda;
            }

            _evaluatable = parentEvaluatable && _evaluatable;
            _containsClosure = parentContainsClosure || _containsClosure;

            return expression;
        }

        protected override Expression VisitLambda<T>(Expression<T> lambdaExpression)
        {
            var oldInLambda = _inLambda;
            _inLambda = true;

            // Note: Don't skip visiting parameter here.
            // SelectMany does not use parameter in lambda but we should still block it from evaluating
            base.VisitLambda(lambdaExpression);

            _inLambda = oldInLambda;
            return lambdaExpression;
        }

        protected override Expression VisitMemberInit(MemberInitExpression memberInitExpression)
        {
            Visit(memberInitExpression.Bindings, VisitMemberBinding);

            // Cannot make parameter for NewExpression if Bindings cannot be evaluated
            // but we still need to visit inside of it.
            var bindingsEvaluatable = _evaluatable;
            Visit(memberInitExpression.NewExpression);

            if (!bindingsEvaluatable)
            {
                _evaluatableExpressions.Remove(memberInitExpression.NewExpression);
            }

            return memberInitExpression;
        }

        protected override Expression VisitListInit(ListInitExpression listInitExpression)
        {
            Visit(listInitExpression.Initializers, VisitElementInit);

            // Cannot make parameter for NewExpression if Initializers cannot be evaluated
            // but we still need to visit inside of it.
            var initializersEvaluatable = _evaluatable;
            Visit(listInitExpression.NewExpression);

            if (!initializersEvaluatable)
            {
                _evaluatableExpressions.Remove(listInitExpression.NewExpression);
            }

            return listInitExpression;
        }

        protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
        {
            Visit(methodCallExpression.Object);
            var parameterInfos = methodCallExpression.Method.GetParameters();
            for (var i = 0; i < methodCallExpression.Arguments.Count; i++)
            {
                if (i == 1
                    && _evaluatableExpressions.ContainsKey(methodCallExpression.Arguments[0])
                    && methodCallExpression.Method.DeclaringType == typeof(Enumerable)
                    && methodCallExpression.Method.Name == nameof(Enumerable.Select)
                    && methodCallExpression.Arguments[1] is LambdaExpression lambdaExpression)
                {
                    // Allow evaluation Enumerable.Select operation
                    foreach (var parameter in lambdaExpression.Parameters)
                    {
                        _allowedParameters.Add(parameter);
                    }
                }

                Visit(methodCallExpression.Arguments[i]);

                if (_evaluatableExpressions.ContainsKey(methodCallExpression.Arguments[i])
                    && (parameterInfos[i].GetCustomAttribute<NotParameterizedAttribute>() != null
                        || _model.IsIndexerMethod(methodCallExpression.Method)))
                {
                    _evaluatableExpressions[methodCallExpression.Arguments[i]] = false;
                }
            }

            return methodCallExpression;
        }

        protected override Expression VisitMember(MemberExpression memberExpression)
        {
            _containsClosure = memberExpression.Expression != null
                || !(memberExpression.Member is FieldInfo { IsInitOnly: true });
            return base.VisitMember(memberExpression);
        }

        protected override Expression VisitParameter(ParameterExpression parameterExpression)
        {
            _evaluatable = _allowedParameters.Contains(parameterExpression);

            return base.VisitParameter(parameterExpression);
        }

        protected override Expression VisitConstant(ConstantExpression constantExpression)
        {
            _evaluatable = !(constantExpression.Value is IQueryable);

#pragma warning disable RCS1096 // Use bitwise operation instead of calling 'HasFlag'.
            _containsClosure
                = (constantExpression.Type.Attributes.HasFlag(TypeAttributes.NestedPrivate)
                    && Attribute.IsDefined(constantExpression.Type, typeof(CompilerGeneratedAttribute), inherit: true)) // Closure
                || constantExpression.Type == typeof(ValueBuffer); // Find method
#pragma warning restore RCS1096 // Use bitwise operation instead of calling 'HasFlag'.

            return base.VisitConstant(constantExpression);
        }

        private static bool IsEvaluatableNodeType(Expression expression, out bool preferNoEvaluation)
        {
            switch (expression.NodeType)
            {
                case ExpressionType.NewArrayInit:
                    preferNoEvaluation = true;
                    return true;

                case ExpressionType.Extension:
                    preferNoEvaluation = false;
                    return expression.CanReduce && IsEvaluatableNodeType(expression.ReduceAndCheck(), out preferNoEvaluation);

                // Identify a call to EF.Constant(), and flag that as non-evaluable.
                // This is important to prevent a larger subtree containing EF.Constant from being evaluated, i.e. to make sure that
                // the EF.Function argument is present in the tree as its own, constant node.
                case ExpressionType.Call
                    when expression is MethodCallExpression { Method: var method }
                    && method.DeclaringType == typeof(EF)
                    && method.Name is nameof(EF.Constant) or nameof(EF.Parameter):
                    preferNoEvaluation = true;
                    return false;

                default:
                    preferNoEvaluation = false;
                    return true;
            }
        }

        private static bool IsQueryableMethod(Expression expression)
            => expression is MethodCallExpression methodCallExpression
                && methodCallExpression.Method.DeclaringType == typeof(Queryable);
    }

    private sealed class EvaluatedValues
    {
        public string? CandidateParameterName { get; init; }
        public object? Value { get; init; }
        public Expression? Constant { get; set; }
        public Expression? Parameter { get; set; }
    }
}

internal static class SharedTypeExtensions
{
    public static Type UnwrapNullableType(this Type type)
        => Nullable.GetUnderlyingType(type) ?? type;
}