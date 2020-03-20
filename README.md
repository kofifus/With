# With

Adds a `With` method to immutable objects that constructs a new 'mutation' of the object with a changed member specified by a lambda expression.

# Simple Usage

Define an immutable class and mark it as such by inheriting interface Immutable:
```
public class Employee : IImmutable {
  public string FirstName { get; }
  public readonly string LastName;

  public Employee(string firstName, string lastName) { FirstName = firstName; LastName = lastName; }
}
```
Note that parameter names in our constructor *must* match fields names except for the first letter which can have a different case (ie `firstName` for field `FirstName`) 

Apply modification:

```
var source = new Employee("John", "Doe");
var mutation1 = source.With(x => x.FirstName, "Foo");
var mutation2 = source.With((x => x.FirstName, "newFirst"), (x => x.LastName, "newLast"));
```

# Nested mutations

`With` can set nested members and will only update references in the mutated path

```
public class Department : IImmutable {
  public string Title { get; }
  public Employee Manager { get; }

  public Department(string title, Employee manager) { Title = title; Manager = manager; }
}

public class Organization : IImmutable {
  public string Name { get; }
  public Department Sales { get; }

  public Organization(string name, Department sales) { Name = name; Sales = sales; }
}

var org = new Organization("Organization", new Department("Development Department", new Employee("John", "Doe")));
var mutatedOrg = org.With(x => x.Sales.Manager.FirstName, "Foo");

Object.ReferenceEquals(org.Sales.Manager, mutatedOrg.Sales.Manager)); // false
Object.ReferenceEquals(org.Sales, mutatedOrg.Sales)); // false
Object.ReferenceEquals(org.Name, mutatedOrg.Name)); // true
Object.ReferenceEquals(org.Sales.Title, mutatedOrg.Sales.Title)); // true
```

# Constructor search

When a type has multiple constructors, `With` will search for a constructor to use for mutation in the following way:

- If there are no consturctors throw an exception
- If there are any consturctors with attribute `[WithConstructor]` consider only them
- Consider constructors in a descending order of number of arguments (most arguments first)
- Look for the matching constructor to use. A constructor matches if each parameter match one of the members in both type and name (name can have a different case for the first letter), and also the memeber(s) being mutated is one of the parameters.
- If `With` is invoked with multiple mutations, try first to find a constructor that can accept _all_ mutated members, if there is no such constructor, try to find a constructor to mutate each member separately. 

# Complex example:

```
public class Employee : IImmutable {
  public string FirstName { get; }
  public readonly string LastName;

  public Employee(string firstName) : this(firstName, "") { }

  [WithConstructor]
  public Employee(string firstName, string lastName) { FirstName = firstName; LastName = lastName; }
}

public class Department : IImmutable {
  public string Title { get; }
  public Employee Manager { get; }
  public DateTime Created { get; }

  public Department() : this("", new Employee("", "")) { }
  public Department(string title, int manager) : this(title, new Employee("", "")) { }
  public Department(string title) : this(title, new Employee("", "")) { }

  // With will choose this ctor 
  public Department(string title, Employee manager) { Title = title; Manager = manager; Created = DateTime.Now; }
}

public class Organization : IImmutable {
  public string Name { get; }
  public Department Sales { get; }

  public Organization(string name) { Name = name; Sales = new Department(); }
  public Organization(string name, Department sales) { Name = name; Sales = sales; }
}


class Program {

  static void Main(string[] args) {
    var expected = new Organization("Organization", new Department("Development Department", new Employee("John", "Doe")));
    var actual = expected.With(x => x.Sales.Manager.FirstName, "Foo");

    Console.WriteLine(expected.Sales.Manager.FirstName);
    Console.WriteLine(actual.Sales.Manager.FirstName);
    Console.WriteLine(Object.ReferenceEquals(expected, actual)); // false
    Console.WriteLine(Object.ReferenceEquals(expected.Sales, actual.Sales)); // false
    Console.WriteLine(Object.ReferenceEquals(expected.Sales.Manager, actual.Sales.Manager)); // false
    Console.WriteLine(Object.ReferenceEquals(expected.Name, actual.Name)); // true
    Console.WriteLine(Object.ReferenceEquals(expected.Sales.Title, expected.Sales.Title)); // true

    var actual1 = expected.With((x => x.Sales.Manager.FirstName, "Foo"), (x => x.Name, "newOrg"));
    Console.WriteLine(Object.ReferenceEquals(actual, actual1)); // false

    Console.ReadKey(true);
  }
}
```

# Notes

- All reflections and lambda invocations are cached with lambda expressions for great efficiency
- This project started as a fork of [Remute](https://github.com/ababik/Remute) and credit goes there
