using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;


namespace System.Immutable {
  // CtorParamResolver = (ctorMemberName, ctorParamResolver)
  using CtorParamResolver = ValueTuple<string, Func<object, object>>;

  [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false)]
  public sealed class WithConstructor : Attribute { }

  class WithPrivate {
    delegate object Activator(params object[] args);
    delegate object InstanceDelegate<T>(T src);

    // key: "{srcType.FullName}|{WithMember}"  
    //  ie: "Employee"
    // ctorActivator: args => new valType(args[0] as ctorParams[0].ParamType, ...) as object
    //            ie: args => new Employee(args[0] as String, args[1] as String) as Object 
    // ctorParamsResolvers: [ DstType(srcTypeProp1.Name, x => (x as srcType).srcTypeProp1) as Object), ... ]
    //                  ie: [ ( "EmployeeFirstName" , x => ((x as Employee).EmployeeFirstName) as Object) ), ( "EmployeeLastName" , x => ((x as Employee).EmployeeLastName) as Object) ) ]
    ImmutableDictionary<string, (Activator ctorActivator, CtorParamResolver[] ctorParamsResolvers)> ActivationContextCache = ImmutableDictionary<string, (Activator ctorActivator, CtorParamResolver[] ctorParamsResolvers)>.Empty;

    // key: "{srcType.FullName}|{WithMember}"  
    //  ie: "test.Organization|DevelopmentDepartment.Manager"
    // val: x => WithMember as Object  
    //    ie: x => x.DevelopmentDepartment.Manager as Object
    ImmutableDictionary<string, Delegate> InstanceDelegateCache = ImmutableDictionary<string, Delegate>.Empty;

    public readonly static WithPrivate Default = new WithPrivate();

    // Contructs immutable object from existing one with changed member specified by lambda expression.
    public TSrc With<TSrc, TVal>(TSrc src, Expression<Func<TSrc, TVal>> expression, TVal value) {
      if (expression is null) throw new ArgumentNullException(nameof(expression));
      if (expression.Parameters.Count() != 1) throw new NotSupportedException("With expression must have a single parameter");

      var parameterExpression = expression.Parameters.First();
      var instanceExpression = expression.Body;
      var val = (object)value;

      while (instanceExpression != parameterExpression) {
        if (!(instanceExpression is MemberExpression memberExpression) || !(memberExpression.Member is MemberInfo instanceExpressionMember))
          throw new NotSupportedException($"Unable to process expression. Expression: '{instanceExpression}'.");

        // find and resolve ctor activator and arguments
        var (ctorActivator, ctorParamsResolvers) = ResolveActivator(instanceExpressionMember);

        instanceExpression = memberExpression.Expression; // go one level up

        // resolve instance 
        var instance = ResolveInstanceDelegate<TSrc>(instanceExpression, parameterExpression).Invoke(src);

        var arguments = new object[ctorParamsResolvers.Length];
        var match = false;

        for (var i = 0; i < ctorParamsResolvers.Length; i++) {
          var (ctorMemberName, ctorParamResolver) = ctorParamsResolvers[i];
          if (ctorMemberName == instanceExpressionMember.Name) {
            arguments[i] = val;
            match = true;
          } else {
            arguments[i] = ctorParamResolver.Invoke(instance);
          }
        }

        if (!match) throw new Exception($"Unable to construct object of type '{instanceExpressionMember.DeclaringType.Name}'. There is no constructor parameter matching member '{instanceExpressionMember.Name}'.");
        val = ctorActivator.Invoke(arguments);

      }

      return (TSrc)val;
    }

    (Activator ctorActivator, CtorParamResolver[]) ResolveActivator(MemberInfo instanceExpressionMember) {
      bool firstLetterCaseInsensitiveCompare(string s1, string s2) {
        if ((s1 is null && s2 is null) || ReferenceEquals(s1, s2)) return true;
        if (s1 is null || s2 is null || s1.Length != s2.Length) return false;
        if (s1.Length == 0) return true;
        if (!s1.Substring(0, 1).Equals(s2.Substring(0, 1), StringComparison.OrdinalIgnoreCase)) return false;
        return s1.Length == 1 || s1.Substring(1).Equals(s2.Substring(1));
      }

      var type = instanceExpressionMember.DeclaringType;
      var cacheKey = type.FullName + "|" + instanceExpressionMember.Name;
      if (ActivationContextCache.TryGetValue(cacheKey, out var res)) return res;

      // get ctors list
      ConstructorInfo[] ctors;
      {
        var allCtors = type.GetTypeInfo().DeclaredConstructors.Where(c => c.GetParameters().Count() > 0);
        var attrCtors = allCtors.Where(c => c.GetCustomAttributes(false).Any(a => a is WithConstructor));
        ctors = (attrCtors.Count() > 0 ? attrCtors : allCtors).ToArray();
      }
      ctors.OrderBy(x => x.GetParameters());

      foreach (var ctor in ctors) {
        var ctorParams = ctor.GetParameters();
        var hasExpressionMember = false;

        // Get ctorParamsResolvers
        var ctorParamsResolvers = new CtorParamResolver[ctorParams.Length];
        {
          var members = type.GetTypeInfo().DeclaredMembers.ToArray();

          var i = 0;
          foreach (var parameter in ctorParams) {
            //var member = members.Where(x => string.Equals(x.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)).SingleOrDefault();
            var member = members.Where(x => firstLetterCaseInsensitiveCompare(x.Name, parameter.Name)).SingleOrDefault();
            if (member is null
                || (member.MemberType is MemberTypes.Field && ((FieldInfo)member).FieldType != parameter.ParameterType)
                || (member.MemberType is MemberTypes.Property && ((PropertyInfo)member).PropertyType != parameter.ParameterType)) break;

            if (firstLetterCaseInsensitiveCompare(member.Name, instanceExpressionMember.Name)) {
              ctorParamsResolvers[i++] = (member.Name, null);
              hasExpressionMember = true;
            } else {
              // calc lambdaExpression: x => (x as srcType).member) as object
              var parameterExpression = Expression.Parameter(typeof(object));
              var parameterConvertExpression = Expression.Convert(parameterExpression, type);
              var memberExpression = Expression.MakeMemberAccess(parameterConvertExpression, member);
              var propertyConvertExpression = Expression.Convert(memberExpression, typeof(object));
              var lambdaExpression = Expression.Lambda<Func<object, object>>(propertyConvertExpression, parameterExpression);
              var ctorParamResolver = lambdaExpression.Compile();

              ctorParamsResolvers[i++] = (member.Name, ctorParamResolver);
            }
          }
          if (i < ctorParams.Length || !hasExpressionMember) continue; // this ctor will not work
        }

        // get activator
        Activator ctorActivator;
        {
          // calc activator: args => (new valType(args[0] as ctorParams[0].ParamType, args[1] as ctorParams[1].ParamType ...) as object
          var parameterExpression = Expression.Parameter(typeof(object[]));
          var argumentExpressions = new Expression[ctorParams.Length];

          for (var i = 0; i < ctorParams.Length; i++) {
            var arrayExpression = Expression.ArrayIndex(parameterExpression, Expression.Constant(i));
            var arrayConvertExpression = Expression.Convert(arrayExpression, ctorParams[i].ParameterType);
            argumentExpressions[i] = arrayConvertExpression;
          }

          var constructorExpression = Expression.New(ctor, argumentExpressions);
          var constructorConvertExpression = Expression.Convert(constructorExpression, typeof(object));
          var activatorLambdaExpression = Expression.Lambda<Activator>(constructorConvertExpression, parameterExpression);
          ctorActivator = activatorLambdaExpression.Compile();
        }

        ActivationContextCache = ActivationContextCache.Add(cacheKey, (ctorActivator, ctorParamsResolvers));
        return (ctorActivator, ctorParamsResolvers);
      }
      throw new Exception($"Unable to find appropriate {type.Name} Constructor for {instanceExpressionMember.Name}."); // no ctor found
    }

    InstanceDelegate<TSrc> ResolveInstanceDelegate<TSrc>(Expression instanceExpression, ParameterExpression parameterExpression) {
      // create unique cache key, calc same key for x=>x.p and y=>y.p
      var exprStr = instanceExpression.ToString();
      var dotPos = exprStr.IndexOf(Type.Delimiter);
      var cacheKey = typeof(TSrc).FullName + '|' + (dotPos > 0 ? exprStr.Remove(0, exprStr.IndexOf(Type.Delimiter) + 1) : "root");

      if (InstanceDelegateCache.TryGetValue(cacheKey, out var instanceDelegate)) return (InstanceDelegate<TSrc>)instanceDelegate;
      var instanceConvertExpression = Expression.Convert(instanceExpression, typeof(object));
      var instanceConvertLambda = Expression.Lambda<InstanceDelegate<TSrc>>(instanceConvertExpression, parameterExpression);
      instanceDelegate = instanceConvertLambda.Compile();

      InstanceDelegateCache = InstanceDelegateCache.SetItem(cacheKey, instanceDelegate);
      return (InstanceDelegate<TSrc>)instanceDelegate;
    }
  }


  public static partial class ExtensionMethods {
    /// <summary>
    /// Contructs immutable object from existing one with changed member specified by lambda expression
    /// </summary>
    /// <param name="expression">Navigation lambda x => member</param>
    /// <param name="value">Mutated Value</param>
    /// <returns></returns>
    public static TSrc With<TSrc, TVal>(this TSrc instance, Expression<Func<TSrc, TVal>> expression, TVal value) where TSrc : IImmutable =>
      WithPrivate.Default.With(instance, expression, value);

    /// <summary>
    /// Contructs immutable object from existing one with changed member specified by lambda expression
    /// </summary>
    /// <param name="expression">Navigation lambda x => member</param>
    /// <param name="valueFunc">Lambda accepting the previous value and returning the new one</param>
    /// <returns></returns>
    public static TSrc With<TSrc, TVal>(this TSrc instance, Expression<Func<TSrc, TVal>> expression, Func<TVal, TVal> valueFunc) where TSrc : IImmutable {
      var oldVal = expression.Compile().Invoke(instance);
      var newVal = valueFunc(oldVal);
      return WithPrivate.Default.With(instance, expression, newVal);
    }
  }

}
