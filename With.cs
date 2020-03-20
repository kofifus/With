using System.Collections.Immutable;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.CompilerServices;

#nullable disable

namespace System.Immutable
{
  public interface IImmutable { };
}

namespace System.Immutable
{
  [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false)]
  public sealed class WithConstructor : Attribute { }

  static class WithPrivate
  {
    delegate object CtorActivator(object instance, object[] values);
    delegate object MemberResolver<T>(T src);

    // CtorActivatorCache caches compiled ctor activations for effiency
    // key: "{srcType.FullName}|{member}|.." ,  ie: "Test.Employee|FirstName|Age
    // ctorActivator: (x, args[]) => { var cx = (Employee)x; return new Employee((String)args[0], cx.LastName, (int)args[1]); }
    static ImmutableDictionary<string, CtorActivator> CtorActivatorCache = ImmutableDictionary<string, CtorActivator>.Empty;

    // MemberResolverCache caches compiled member access lambdas for efficiency 
    // key: "{srcType.FullName}|{member}" , ie: "Test.Organization|DevelopmentDepartment.Manager"
    // delegate: x => x.DevelopmentDepartment.Manager as Object
    static ImmutableDictionary<string, Delegate> MemberResolverCache = ImmutableDictionary<string, Delegate>.Empty;

    // Contructs immutable object from existing one with changed member specified by lambda expression.
    static public TSrc With<TSrc>(TSrc src, params (LambdaExpression expression, object val)[] withPairs) 
    {
      var memberNames = new string[withPairs.Length];
      var memberValues = new object[withPairs.Length];

      // sanity checks
      if (src is null) throw new NotSupportedException("source is null");
      foreach (var (withExpression, _) in withPairs) {
        if (withExpression is null) throw new ArgumentNullException(nameof(withExpression));
        if (withExpression.Parameters.Count() != 1) throw new NotSupportedException("With expression must have a single parameter");
      }

      for (var i = 0; i < withPairs.Length; i++) {
        var (withExpression, withValue) = withPairs[i];

        var parameterExpression = withExpression.Parameters.First();
        var instanceExpression = withExpression.Body;
        var val = (object)withValue;

        // roll all nested member access to the top
        while (true) {
          if (!(instanceExpression is MemberExpression memberExpression) || !(memberExpression.Member is MemberInfo memberInfo))
            throw new NotSupportedException($"Unable to process expression. Expression: '{instanceExpression}'.");

          memberNames[i] = memberInfo.Name;
          memberValues[i] = val;

          if (memberExpression.Expression == parameterExpression) break; // we're done
          if (memberInfo.DeclaringType is null) throw new NotSupportedException($"memberInfo.DeclaringType is null");

          // resolve instance and activator and invoke
          var instance = GetInstance<TSrc>(memberExpression.Expression, parameterExpression).Invoke(src);
          var (ok, ctorActivator) = GetActivator(memberInfo.DeclaringType, new string[] { memberInfo.Name });
          if (!ok) throw new Exception($"Unable to find {memberInfo.DeclaringType?.Name} constructor (when trying to mutate {memberInfo.Name})."); // no ctor found
          val = ctorActivator.Invoke(instance, new object[] { val });

          // go one level up
          instanceExpression = memberExpression.Expression; 
        }
      }

      var srcType = typeof(TSrc);

      // try to find a single ctor for all mutations
      if (withPairs.Length > 1) {
        var (ok, ctorActivator) = GetActivator(srcType, memberNames);
        if (ok) return (TSrc)ctorActivator.Invoke(src, memberValues);

        #if DEBUG
          Debug.WriteLine($"No single ctor for: {srcType}({string.Join(", ", memberNames)})");
        #endif
      }

      // mutate one by one
      TSrc res = src;
      for (var i=0; i<withPairs.Length; i++) { 
        var (ok, ctorActivator) = GetActivator(typeof(TSrc), new string[] { memberNames[i] });
        if (!ok) throw new Exception($"Unable to find {srcType.Name} constructor (when trying to mutate {memberNames[i]})."); // no ctor found
        res = (TSrc)ctorActivator.Invoke(res, new object[] { memberValues[i] });
      }

      return res;
    }

    static (bool, CtorActivator) GetActivator(Type type, string[] memberNames) 
    {
      var cacheKey = type?.FullName + memberNames.Aggregate("", (total, next) => total + "|" + next); 

      if (CtorActivatorCache.TryGetValue(cacheKey, out var ctorActivatr)) return (true, ctorActivatr); // found in cache

      // get type members
      var members = type?.GetTypeInfo()
        .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(memberInfo => memberInfo is PropertyInfo || (memberInfo is FieldInfo && memberInfo.GetCustomAttribute<CompilerGeneratedAttribute>() == null)) // filter out backing members
        .ToArray();

      // get ctors list
      var allCtors = type?.GetTypeInfo().DeclaredConstructors.Where(c => c.GetParameters().Count() > 0);
      var attrCtors = allCtors.Where(c => c.GetCustomAttributes(false).Any(a => a is WithConstructor));
      var ctors = (attrCtors.Count() > 0 ? attrCtors : allCtors);
      ctors = ctors.OrderByDescending(x => x.GetParameters().Length);

      var x = Expression.Parameter(typeof(object), "x");
      var xAsType = Expression.Parameter(type, "cx");
      var argsArray = Expression.Parameter(typeof(object[]), "vars");
      var argsElements = memberNames.Select((_, i) => Expression.ArrayIndex(argsArray, Expression.Constant(i, typeof(int)))).ToArray();

      foreach (var ctor in ctors) {
        var ctorParams = ctor.GetParameters();
        var nMatches = 0;
        var ctorParamsExpressions = new Expression[ctorParams.Length];

        var i = 0;
        foreach (var paramInfo in ctorParams) {
          // get matching member where the name matches (except for first letter case) and the type matches
          var matchingMembersInfo = members.Where(memberInfo => 
            FirstLetterCaseInsensitiveCompare(memberInfo.Name, paramInfo.Name ?? "")
            && ((memberInfo.MemberType is MemberTypes.Field && ((FieldInfo)memberInfo).FieldType.Equals(paramInfo.ParameterType))
               || (memberInfo.MemberType is MemberTypes.Property && ((PropertyInfo)memberInfo).PropertyType.Equals(paramInfo.ParameterType))));
          if (matchingMembersInfo.Count()!=1) break; // this ctor does not have a match between parameters and members, skip it
          var memberInfo = matchingMembersInfo.First();

          var matchIndex = Array.FindIndex(memberNames, x => x.Equals(memberInfo.Name));
          if (matchIndex!=-1) { 
            // this is the mutated member
            ctorParamsExpressions[i++] = Expression.Convert(argsElements[matchIndex], paramInfo.ParameterType); // v as param type
            nMatches++;
          } else { 
            // use existing member for parameter
            ctorParamsExpressions[i++] = Expression.MakeMemberAccess(xAsType, memberInfo); // cx.member
          }
        }
        if (i < ctorParams.Length || nMatches!=memberNames.Length) continue; // this ctor will not work

        // we found a ctor, calc ctorActivator:
        //   (object x, object[] args) => { var cx = (type)x; return new type(cx.member1, ..., args[0], ...); }
        var activatorBody = Expression.Block(
          new ParameterExpression[] { xAsType },
          Expression.Assign(xAsType, Expression.Convert(x, type)),
          Expression.New(ctor, ctorParamsExpressions));
        var activatorParameters = new ParameterExpression[] { x, argsArray };
        CtorActivator ctorActivator = Expression.Lambda<CtorActivator>(activatorBody, activatorParameters).Compile();

        #if DEBUG
          Debug.WriteLine($"CtorActivatorCache Caching key: {cacheKey}");
          Debug.WriteLine($"  (object x, object[] vars) => {{ {activatorBody.Expressions[0]}; return {activatorBody.Expressions[1]}; }}");
        #endif

        CtorActivatorCache = CtorActivatorCache.Add(cacheKey, ctorActivator); // cache it
        return (true, ctorActivator);
      }

      return (false, default); // could not find a ctor
    }

    static MemberResolver<TSrc> GetInstance<TSrc>(Expression instanceExpression, ParameterExpression parameterExpression)
    {
      // create unique cache key, calc same key for x=>x.p and y=>y.p
      var exprStr = instanceExpression.ToString();
      var dotPos = exprStr.IndexOf(Type.Delimiter);
      var cacheKey = typeof(TSrc).FullName + '|' + (dotPos > 0 ? exprStr.Remove(0, exprStr.IndexOf(Type.Delimiter) + 1) : "root");
       
      if (MemberResolverCache.TryGetValue(cacheKey, out var memberResolverDelegate)) return (MemberResolver<TSrc>)memberResolverDelegate; // found in cache

      var instanceConvertExpression = Expression.Convert(instanceExpression, typeof(object));
      var instanceConvertLambda = Expression.Lambda<MemberResolver<TSrc>>(instanceConvertExpression, parameterExpression);
      memberResolverDelegate = instanceConvertLambda.Compile();

      #if DEBUG
        Debug.WriteLine($"MemberResolverCache Caching key: {cacheKey}");
        Debug.WriteLine($"  {instanceConvertLambda}");
      #endif

      MemberResolverCache = MemberResolverCache.Add(cacheKey, memberResolverDelegate);
      return (MemberResolver<TSrc>)memberResolverDelegate;
    }

    static bool FirstLetterCaseInsensitiveCompare(string s1, string s2)
    {
      if ((s1 is null && s2 is null) || ReferenceEquals(s1, s2)) return true;
      if (s1 is null || s2 is null || s1.Length != s2.Length) return false;
      if (s1.Length == 0) return true;
      if (!s1.Substring(0, 1).Equals(s2.Substring(0, 1), StringComparison.OrdinalIgnoreCase)) return false;
      return s1.Length == 1 || s1.Substring(1).Equals(s2.Substring(1));
    }
  }


  public static partial class ExtensionMethods
  {
    // mutate a single member
    public static TSrc With<TSrc, TVal>(this TSrc instance, Expression<Func<TSrc, TVal>> exp, TVal val) where TSrc : IImmutable =>  WithPrivate.With(instance, (exp, val));

    // mutate multiple members
    public static TSrc With<TSrc, TVal1>(this TSrc instance, (Expression<Func<TSrc, TVal1>>, TVal1) vt1) where TSrc : IImmutable => WithPrivate.With(instance, vt1);
    public static TSrc With<TSrc, TVal1, TVal2>(this TSrc instance, (Expression<Func<TSrc, TVal1>>, TVal1) vt1, (Expression<Func<TSrc, TVal2>>, TVal2) vt2) where TSrc : IImmutable => WithPrivate.With(instance, vt1, vt2);
    public static TSrc With<TSrc, TVal1, TVal2, TVal3>(this TSrc instance, (Expression<Func<TSrc, TVal1>> , TVal1 ) vt1, (Expression<Func<TSrc, TVal2>> , TVal2 ) vt2, (Expression<Func<TSrc, TVal3>> , TVal3 ) vt3) where TSrc : IImmutable => WithPrivate.With(instance, vt1, vt2, vt3);
    public static TSrc With<TSrc, TVal1, TVal2, TVal3, TVal4>(this TSrc instance, (Expression<Func<TSrc, TVal1>> , TVal1 ) vt1, (Expression<Func<TSrc, TVal2>> , TVal2 ) vt2, (Expression<Func<TSrc, TVal3>> , TVal3 ) vt3, (Expression<Func<TSrc, TVal4>> , TVal4 ) vt4) where TSrc : IImmutable => WithPrivate.With(instance, vt1, vt2, vt3, vt4);
    public static TSrc With<TSrc, TVal1, TVal2, TVal3, TVal4, TVal5>(this TSrc instance, (Expression<Func<TSrc, TVal1>> , TVal1 ) vt1, (Expression<Func<TSrc, TVal2>> , TVal2 ) vt2, (Expression<Func<TSrc, TVal3>> , TVal3 ) vt3, (Expression<Func<TSrc, TVal4>> , TVal4 ) vt4, (Expression<Func<TSrc, TVal5>> , TVal5 ) vt5) where TSrc : IImmutable => WithPrivate.With(instance, vt1, vt2, vt3, vt4, vt5);
    public static TSrc With<TSrc, TVal1, TVal2, TVal3, TVal4, TVal5, TVal6>(this TSrc instance, (Expression<Func<TSrc, TVal1>> , TVal1 ) vt1, (Expression<Func<TSrc, TVal2>> , TVal2 ) vt2, (Expression<Func<TSrc, TVal3>> , TVal3 ) vt3, (Expression<Func<TSrc, TVal4>> , TVal4 ) vt4, (Expression<Func<TSrc, TVal5>> , TVal5 ) vt5, (Expression<Func<TSrc, TVal6>> , TVal6 ) vt6) where TSrc : IImmutable => WithPrivate.With(instance, vt1, vt2, vt3, vt4, vt5, vt6);
    public static TSrc With<TSrc, TVal1, TVal2, TVal3, TVal4, TVal5, TVal6, TVal7>(this TSrc instance, (Expression<Func<TSrc, TVal1>> , TVal1 ) vt1, (Expression<Func<TSrc, TVal2>> , TVal2 ) vt2, (Expression<Func<TSrc, TVal3>> , TVal3 ) vt3, (Expression<Func<TSrc, TVal4>> , TVal4 ) vt4, (Expression<Func<TSrc, TVal5>> , TVal5 ) vt5, (Expression<Func<TSrc, TVal6>> , TVal6 ) vt6, (Expression<Func<TSrc, TVal7>> , TVal7 ) vt7) where TSrc : IImmutable => WithPrivate.With(instance, vt1, vt2, vt3, vt4, vt5, vt6, vt7);
    public static TSrc With<TSrc, TVal1, TVal2, TVal3, TVal4, TVal5, TVal6, TVal7, TVal8>(this TSrc instance, (Expression<Func<TSrc, TVal1>> , TVal1 ) vt1, (Expression<Func<TSrc, TVal2>> , TVal2 ) vt2, (Expression<Func<TSrc, TVal3>> , TVal3 ) vt3, (Expression<Func<TSrc, TVal4>> , TVal4 ) vt4, (Expression<Func<TSrc, TVal5>> , TVal5 ) vt5, (Expression<Func<TSrc, TVal6>> , TVal6 ) vt6, (Expression<Func<TSrc, TVal7>> , TVal7 ) vt7, (Expression<Func<TSrc, TVal8>> , TVal8 ) vt8) where TSrc : IImmutable => WithPrivate.With(instance, vt1, vt2, vt3, vt4, vt5, vt6, vt7, vt8);
    public static TSrc With<TSrc, TVal1, TVal2, TVal3, TVal4, TVal5, TVal6, TVal7, TVal8, TVal9>(this TSrc instance, (Expression<Func<TSrc, TVal1>> , TVal1 ) vt1, (Expression<Func<TSrc, TVal2>> , TVal2 ) vt2, (Expression<Func<TSrc, TVal3>> , TVal3 ) vt3, (Expression<Func<TSrc, TVal4>> , TVal4 ) vt4, (Expression<Func<TSrc, TVal5>> , TVal5 ) vt5, (Expression<Func<TSrc, TVal6>> , TVal6 ) vt6, (Expression<Func<TSrc, TVal7>> , TVal7 ) vt7, (Expression<Func<TSrc, TVal8>> , TVal8 ) vt8, (Expression<Func<TSrc, TVal9>> , TVal9 ) vt9) where TSrc : IImmutable => WithPrivate.With(instance, vt1, vt2, vt3, vt4, vt5, vt6, vt7, vt8, vt9);
  }
}
