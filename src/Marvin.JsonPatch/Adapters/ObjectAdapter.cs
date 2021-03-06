﻿// Any comments, input: @KevinDockx
// Any issues, requests: https://github.com/KevinDockx/JsonPatch
//
// Enjoy :-)

using Marvin.JsonPatch.Exceptions;
using Marvin.JsonPatch.Helpers;
using Marvin.JsonPatch.Operations;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;

namespace Marvin.JsonPatch.Adapters
{
    public class ObjectAdapter<T> : IObjectAdapter<T> where T : class
    {
        public CustomObjectDeserializeHandler CustomDeserializationHandler { get; set; }
        public event BeforeSetValueHandler BeforeSetValue;

        protected void InvokeWithBeforeSetValue(SetValueEventArgs eArg, Action action)
        {
            if (BeforeSetValue != null)
            {
                BeforeSetValue(this, eArg);
            }
            if (!eArg.Cancel)
            {
                action.Invoke();
            }
        }

        public IContractResolver ContractResolver { get; private set; }

        /// <summary>
        /// Instantiate a new ObjectAdapter
        /// </summary>
        public ObjectAdapter()
        {
            ContractResolver = new DefaultContractResolver();
        }

        /// <summary>
        /// Instantiate a new ObjectAdapter, passing in a custom IContractResolver
        /// </summary>
        public ObjectAdapter(IContractResolver contractResolver)
        {
            ContractResolver = contractResolver;
        }

        /// <summary>
        /// The "add" operation performs one of the following functions,
        /// depending upon what the target location references:
        /// 
        /// o  If the target location specifies an array index, a new value is
        ///    inserted into the array at the specified index.
        /// 
        /// o  If the target location specifies an object member that does not
        ///    already exist, a new member is added to the object.
        /// 
        /// o  If the target location specifies an object member that does exist,
        ///    that member's value is replaced.
        /// 
        /// The operation object MUST contain a "value" member whose content
        /// specifies the value to be added.
        /// 
        /// For example:
        /// 
        /// { "op": "add", "path": "/a/b/c", "value": [ "foo", "bar" ] }
        /// 
        /// When the operation is applied, the target location MUST reference one
        /// of:
        /// 
        /// o  The root of the target document - whereupon the specified value
        ///    becomes the entire content of the target document.
        /// 
        /// o  A member to add to an existing object - whereupon the supplied
        ///    value is added to that object at the indicated location.  If the
        ///    member already exists, it is replaced by the specified value.
        /// 
        /// o  An element to add to an existing array - whereupon the supplied
        ///    value is added to the array at the indicated location.  Any
        ///    elements at or above the specified index are shifted one position
        ///    to the right.  The specified index MUST NOT be greater than the
        ///    number of elements in the array.  If the "-" character is used to
        ///    index the end of the array (see [RFC6901]), this has the effect of
        ///    appending the value to the array.
        /// 
        /// Because this operation is designed to add to existing objects and
        /// arrays, its target location will often not exist.  Although the
        /// pointer's error handling algorithm will thus be invoked, this
        /// specification defines the error handling behavior for "add" pointers
        /// to ignore that error and add the value as specified.
        /// 
        /// However, the object itself or an array containing it does need to
        /// exist, and it remains an error for that not to be the case.  For
        /// example, an "add" with a target location of "/a/b" starting with this
        /// document:
        /// 
        /// { "a": { "foo": 1 } }
        /// 
        /// is not an error, because "a" exists, and "b" will be added to its
        /// value.  It is an error in this document:
        /// 
        /// { "q": { "bar": 2 } }
        /// 
        /// because "a" does not exist.
        /// </summary>
        /// <param name="operation">The add operation</param>
        /// <param name="objectApplyTo">Object to apply the operation to</param>
        public void Add(Operation<T> operation, T objectToApplyTo)
        {
            Add(operation.path, operation.value, objectToApplyTo, operation);
        }

        /// <summary>
        /// Add is used by various operations (eg: add, copy, ...), yet through different operations;
        /// This method allows code reuse yet reporting the correct operation on error
        /// </summary>
        private void Add(string path, object value, T objectToApplyTo, Operation<T> operationToReport)
        {
            // add, in this implementation, does not just "add" properties - that's
            // technically impossible;  It can however be used to add items to arrays,
            // or to replace values.

            // first up: if the path ends in a numeric value, we're inserting in a list and
            // that value represents the position; if the path ends in "-", we're appending
            // to the list.

            // get path result
            var pathResult = PropertyHelpers.GetActualPropertyPath(
                path,
                objectToApplyTo,
                operationToReport,
                true);

            var appendList = pathResult.ExecuteAtEnd;
            var positionAsInteger = pathResult.NumericEnd;
            var actualPathToProperty = pathResult.PathToProperty;
                       
            var result = new ObjectTreeAnalysisResult(objectToApplyTo, actualPathToProperty, ContractResolver);

            if (!result.IsValidPathForAdd)
            {
                throw new JsonPatchException(
                       new JsonPatchError(
                           objectToApplyTo,
                           operationToReport,
                           string.Format("Patch failed: the provided path is invalid: {0}.", path)),
                       422);
            }

            // If it' an array, add to that array.  If it's not, we replace.

            // is the path an array (but not a string (= char[]))?  In this case,
            // the path must end with "/position" or "/-", which we already determined before.

            var patchProperty = result.JsonPatchProperty;

            if (appendList || positionAsInteger > -1)
            {
                if (PropertyHelpers.IsNonStringList(patchProperty.Property.PropertyType))
                {
                    // now, get the generic type of the enumerable
                    var genericTypeOfArray = PropertyHelpers.GetEnumerableType(patchProperty.Property.PropertyType);

                    var conversionResult = PropertyHelpers.ConvertToActualType(genericTypeOfArray, value);

                    if (!conversionResult.CanBeConverted)
                    {
                        throw new JsonPatchException(
                          new JsonPatchError(
                              objectToApplyTo,
                              operationToReport,
                              string.Format("Patch failed: provided value is invalid for array property type at location path: {0}", path)),
                          422);
                    }

                    if (patchProperty.Property.Readable)
                    {
                        var array = (IList)patchProperty.Property.ValueProvider
                            .GetValue(patchProperty.Parent);
                        if (array == null)
                        {
                            array = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(genericTypeOfArray));
                            patchProperty.Property.ValueProvider.SetValue(patchProperty.Parent, array);
                        }
                        if (appendList)
                        {
                            SetValueEventArgs eArg = new SetValueEventArgs(operationToReport)
                            {
                                Cancel = false,
                                Index = array.Count + 1,
                                NewValue = conversionResult.ConvertedInstance,
                                ParentObject = patchProperty.Parent,
                                Property = patchProperty.Property
                            };
                            InvokeWithBeforeSetValue(eArg, () => array.Add(eArg.NewValue));
                        }
                        else
                        {
                            // specified index must not be greater than the amount of items in the
                            // array
                            if (positionAsInteger <= array.Count)
                            {
                                SetValueEventArgs eArg = new SetValueEventArgs(operationToReport)
                                {
                                    Cancel = false,
                                    Index = positionAsInteger,
                                    NewValue = conversionResult.ConvertedInstance,
                                    ParentObject = patchProperty.Parent,
                                    Property = patchProperty.Property
                                };
                                InvokeWithBeforeSetValue(eArg, () => array.Insert(positionAsInteger, eArg.NewValue));
                            }
                            else
                            {
                                throw new JsonPatchException(
                                    new JsonPatchError(
                                      objectToApplyTo,
                                      operationToReport,
                                      string.Format("Patch failed: provided path is invalid for array property type at location path: {0}: position larger than array size", path)),
                                    422);
                            }
                        }
                    }
                    else
                    {
                        // cannot read the property
                        throw new JsonPatchException(
                            new JsonPatchError(
                              objectToApplyTo,
                              operationToReport,
                              string.Format("Patch failed: cannot get property value at path {0}.  Possible cause: the property doesn't have an accessible getter.", path)),
                            422);
                    }
                }
                else if (PropertyHelpers.IsNonStringCollection(patchProperty.Property.PropertyType))
                {
                    // we need to do a little work with collections here. All generic collections have an "Add" operation. In this case, we should ONLY
                    // support adding to the end.
                    if (!(positionAsInteger == -1 || appendList))
                    {
                        throw new JsonPatchException(
                                    new JsonPatchError(
                                      objectToApplyTo,
                                      operationToReport,
                                      string.Format("Patch failed: provided path is invalid for array property type at location path: {0}: can only insert at end of array when target type is ICollection", path)),
                                    422);
                    }
                    var genericTypeOfCollection = PropertyHelpers.GetEnumerableType(patchProperty.Property.PropertyType);
                    ConversionResult conversionResult;// = PropertyHelpers.ConvertToActualType(genericTypeOfCollection, value);

                    if (!genericTypeOfCollection.IsValueType && genericTypeOfCollection != typeof(string) && value != null && value.GetType() == typeof(string))
                    {
                        object newObj;
                        if (CustomDeserializationHandler != null)
                        {
                            newObj = CustomDeserializationHandler(value.ToString(), genericTypeOfCollection);
                        }
                        else
                        {
                            newObj = JsonConvert.DeserializeObject(value.ToString(), genericTypeOfCollection);
                        }
                        conversionResult = new ConversionResult(true, newObj);
                    }
                    else if (value != null && genericTypeOfCollection.IsAssignableFrom(value.GetType()))
                    {
                        conversionResult = new ConversionResult(true, value);
                    }
                    else
                    {
                        conversionResult = PropertyHelpers.ConvertToActualType(
                            genericTypeOfCollection,
                            value);
                    }

                    if (!conversionResult.CanBeConverted)
                    {
                        throw new JsonPatchException(
                          new JsonPatchError(
                              objectToApplyTo,
                              operationToReport,
                              string.Format("Patch failed: provided value is invalid for array property type at location path: {0}", path)),
                          422);
                    }
                    if (patchProperty.Property.Readable)
                    {
                        var array = patchProperty.Property.ValueProvider
                            .GetValue(patchProperty.Parent);
                        if (array == null)
                        {
                            // the array was null. This might mean we are dealing with a fresh object. This is okay. Lets initialize it with a basic collection.
                            array = Activator.CreateInstance(typeof(Collection<>).MakeGenericType(genericTypeOfCollection));
                            patchProperty.Property.ValueProvider.SetValue(patchProperty.Parent, array);
                        }
                        var addMethod = patchProperty.Property.PropertyType.GetMethod("Add");
                        SetValueEventArgs eArg = new SetValueEventArgs(operationToReport)
                        {
                            Cancel = false,
                            Index = -1,
                            NewValue = conversionResult.ConvertedInstance,
                            ParentObject = patchProperty.Parent,
                            Property = patchProperty.Property
                        };
                        InvokeWithBeforeSetValue(eArg, () => addMethod.Invoke(array, new object[] { eArg.NewValue }));
                       
                    }
                    else
                    {
                        // cannot read the property
                        throw new JsonPatchException(
                            new JsonPatchError(
                              objectToApplyTo,
                              operationToReport,
                              string.Format("Patch failed: cannot get property value at path {0}.  Possible cause: the property doesn't have an accessible getter.", path)),
                            422);
                    }


                }
                else
                {
                    throw new JsonPatchException(
                        new JsonPatchError(
                          objectToApplyTo,
                          operationToReport,
                          string.Format("Patch failed: provided path is invalid for array property type at location path: {0}: expected array", path)),
                        422);
                }
            }
            else
            {
                Type propType = patchProperty.Property.PropertyType;
                ConversionResult conversionResultTuple;
                if (!propType.IsValueType && propType != typeof(string) && value != null && value.GetType() == typeof(string))
                {
                    object newObj;
                    if (CustomDeserializationHandler != null)
                    {
                        newObj = CustomDeserializationHandler(value.ToString(), propType);
                    }
                    else
                    {
                        newObj = JsonConvert.DeserializeObject(value.ToString(), propType);
                    }
                    conversionResultTuple = new ConversionResult(true, newObj);
                }
                else if (value != null && patchProperty.Property.PropertyType.IsAssignableFrom(value.GetType()))
                {
                    conversionResultTuple = new ConversionResult(true, value);
                }
                else
                {
                    conversionResultTuple = PropertyHelpers.ConvertToActualType(
                        patchProperty.Property.PropertyType,
                        value);
                }

                if (conversionResultTuple.CanBeConverted)
                {
                    if (patchProperty.Property.Writable)
                    {
                        SetValueEventArgs eArg = new SetValueEventArgs(operationToReport)
                        {
                            Cancel = false,
                            NewValue = conversionResultTuple.ConvertedInstance,
                            ParentObject = patchProperty.Parent,
                            Property = patchProperty.Property
                        };
                        InvokeWithBeforeSetValue(eArg, () => patchProperty.Property.ValueProvider.SetValue(
                           eArg.ParentObject,
                           eArg.NewValue));
                    }
                    else
                    {
                        throw new JsonPatchException(
                           new JsonPatchError(
                             objectToApplyTo,
                             operationToReport,
                             string.Format("Patch failed: property at path location cannot be set: {0}.  Possible causes: the property may not have an accessible setter, or the property may be part of an anonymous object (and thus cannot be changed after initialization).", path)),
                           422);
                    }
                }
                else
                {
                    throw new JsonPatchException(
                              new JsonPatchError(
                                objectToApplyTo,
                                operationToReport,
                                string.Format("Patch failed: property value cannot be converted to type of path location {0}.", path)),
                              422);
                }
            }
        }

        /// <summary>
        /// The "move" operation removes the value at a specified location and
        /// adds it to the target location.
        /// 
        /// The operation object MUST contain a "from" member, which is a string
        /// containing a JSON Pointer value that references the location in the
        /// target document to move the value from.
        /// 
        /// The "from" location MUST exist for the operation to be successful.
        /// 
        /// For example:
        /// 
        /// { "op": "move", "from": "/a/b/c", "path": "/a/b/d" }
        /// 
        /// This operation is functionally identical to a "remove" operation on
        /// the "from" location, followed immediately by an "add" operation at
        /// the target location with the value that was just removed.
        /// 
        /// The "from" location MUST NOT be a proper prefix of the "path"
        /// location; i.e., a location cannot be moved into one of its children.
        /// </summary>
        /// <param name="operation">The move operation</param>
        /// <param name="objectApplyTo">Object to apply the operation to</param>
        public void Move(Operation<T> operation, T objectToApplyTo)
        {
            var valueAtFromLocationResult = 
                GetValueAtLocation(operation.from, objectToApplyTo, operation);

            if (valueAtFromLocationResult.HasError)
            {
                // currently not applicable, will throw exception in GetValueAtLocation method
            }

            // remove that value
            var removeResult = Remove(operation.from, objectToApplyTo, operation);

            if (removeResult.HasError)
            {
                // return => currently not applicable, will throw exception in Remove method
            }

            // add that value to the path location
            Add(operation.path,
                valueAtFromLocationResult.PropertyValue,
                objectToApplyTo,
                operation);
        }
         
        /// <summary>
        /// The "remove" operation removes the value at the target location.
        ///
        /// The target location MUST exist for the operation to be successful.
        /// 
        /// For example:
        /// 
        /// { "op": "remove", "path": "/a/b/c" }
        /// 
        /// If removing an element from an array, any elements above the
        /// specified index are shifted one position to the left.
        /// </summary>
        /// <param name="operation">The remove operation</param>
        /// <param name="objectApplyTo">Object to apply the operation to</param>
        public void Remove(Operation<T> operation, T objectToApplyTo)
        {
            Remove(operation.path, objectToApplyTo, operation);
        }

        /// <summary>
        /// Remove is used by various operations (eg: remove, move, ...), yet through different operations;
        /// This method allows code reuse yet reporting the correct operation on error
        /// </summary>
        private RemovedPropertyTypeResult Remove(string path, T objectToApplyTo, Operation<T> operationToReport)
        {
            // get path result
            var pathResult = PropertyHelpers.GetActualPropertyPath(
                path,
                objectToApplyTo,
                operationToReport,
                false);

            var removeFromList = pathResult.ExecuteAtEnd;
            var positionAsInteger = pathResult.NumericEnd;
            var actualPathToProperty = pathResult.PathToProperty;
            var expressionToUse = pathResult.ExpressionEnd;
            var result = new ObjectTreeAnalysisResult(objectToApplyTo, actualPathToProperty, 
                ContractResolver);

            if (!result.IsValidPathForRemove)
            {
                throw new JsonPatchException(
                       new JsonPatchError(
                         objectToApplyTo,
                         operationToReport,
                         string.Format("Patch failed: the provided path is invalid: {0}.", path)),
                       422);
            }

            var patchProperty = result.JsonPatchProperty;

            if (removeFromList || positionAsInteger > -1)
            {
                if (expressionToUse != null)
                {
                    // we have an expression. 
                    var elementType = PropertyHelpers.GetCollectionType(patchProperty.Property.PropertyType);
                    object coll = patchProperty.Property.ValueProvider
                               .GetValue(patchProperty.Parent);
                    var remMethod = typeof(ICollection<>).MakeGenericType(elementType).GetMethod("Remove");
                    var objToRemove = ExpressionHelpers.GetElementAtFromObjectExpression(coll, expressionToUse);
                    var remResult = remMethod.Invoke(coll, new object[] { objToRemove });
                    if ((bool)remResult != true)
                    {
                        throw new Exception("D'oh");
                    }
                    return new RemovedPropertyTypeResult(elementType, false);
                }
                else if (PropertyHelpers.IsNonStringList(patchProperty.Property.PropertyType))
                {
                    // now, get the generic type of the enumerable
                    var genericTypeOfArray = PropertyHelpers.GetEnumerableType(patchProperty.Property.PropertyType);
                                 
                    if (patchProperty.Property.Readable)
                    {
                        var array = (IList)patchProperty.Property.ValueProvider
                               .GetValue(patchProperty.Parent);

                        if (removeFromList)
                        {
                            if (array.Count == 0)
                            {
                                // if the array is empty, we should throw an error
                                throw new JsonPatchException(
                                    new JsonPatchError(
                                      objectToApplyTo,
                                      operationToReport,
                                      string.Format("Patch failed: provided path is invalid for array property type at location path: {0}: position larger than array size", path)),
                                    422);
                            }
                            SetValueEventArgs eArg = new SetValueEventArgs(operationToReport)
                            {
                                Cancel = false,
                                //NewValue = conversionResultTuple.ConvertedInstance,
                                ParentObject = patchProperty.Parent,
                                Property = patchProperty.Property,
                                Index = array.Count - 1,
                            };
                            InvokeWithBeforeSetValue(eArg, () => array.RemoveAt(eArg.Index.Value));

                            // return the type of the value that has been removed
                            return new RemovedPropertyTypeResult(genericTypeOfArray, false);
                        }
                        else
                        {
                            if (positionAsInteger >= array.Count)
                            {
                                throw new JsonPatchException(
                                        new JsonPatchError(
                                          objectToApplyTo,
                                          operationToReport,
                                          string.Format("Patch failed: provided path is invalid for array property type at location path: {0}: position larger than array size", path)),
                                        422);
                            }
                            SetValueEventArgs eArg = new SetValueEventArgs(operationToReport)
                            {
                                Cancel = false,
                                //NewValue = conversionResultTuple.ConvertedInstance,
                                ParentObject = patchProperty.Parent,
                                Property = patchProperty.Property,
                                Index = positionAsInteger
                            };
                            InvokeWithBeforeSetValue(eArg, () => array.RemoveAt(eArg.Index.Value));

                            // return the type of the value that has been removed
                            return new RemovedPropertyTypeResult(genericTypeOfArray, false);
                        }
                    }
                    else
                    {
                        throw new JsonPatchException(
                             new JsonPatchError(
                               objectToApplyTo,
                               operationToReport,
                               string.Format("Patch failed: cannot get property value at path {0}.  Possible cause: the property doesn't have an accessible getter.", path)),
                             422);
                    }
                }
                else
                {
                    throw new JsonPatchException(
                         new JsonPatchError(
                           objectToApplyTo,
                           operationToReport,
                           string.Format("Patch failed: provided path is invalid for array property type at location path: {0}: expected array.", path)),
                         422);
                }
            }
            else
            {
                if (!patchProperty.Property.Writable)
                {
                    throw new JsonPatchException(
                           new JsonPatchError(
                             objectToApplyTo,
                             operationToReport,
                             string.Format("Patch failed: property at path location cannot be set: {0}.  Possible causes: the property may not have an accessible setter, or the property may be part of an anonymous object (and thus cannot be changed after initialization).", path)),
                           422);
                }           

                // set value to null, or for non-nullable value types, to its default value.
                object value = null;

                if (patchProperty.Property.PropertyType.GetType().IsValueType
                    && Nullable.GetUnderlyingType(patchProperty.Property.PropertyType) == null)
                {
                    value = Activator.CreateInstance(patchProperty.Property.PropertyType);
                }

                // check if it can be converted.  
                var conversionResultTuple = PropertyHelpers.ConvertToActualType(
                   patchProperty.Property.PropertyType,
                   value);
                SetValueEventArgs eArg = new SetValueEventArgs(operationToReport)
                {
                    Cancel = false,
                    //NewValue = conversionResultTuple.ConvertedInstance,
                    ParentObject = patchProperty.Parent,
                    Property = patchProperty.Property,
                    IsRemoveStepOfReplace = operationToReport.OperationType == OperationType.Replace

                };
                
                if (!conversionResultTuple.CanBeConverted)
                {
                    // conversion failed, so use reflection (somewhat slower) to 
                    // create a new default instance of the property type to set as value

                    eArg.NewValue = Activator.CreateInstance(patchProperty.Property.PropertyType);
                    //patchProperty.Property.ValueProvider.SetValue(patchProperty.Parent,
                    //    Activator.CreateInstance(patchProperty.Property.PropertyType)); 
                            
                    //return new RemovedPropertyTypeResult(patchProperty.Property.PropertyType, false);
                }
                else
                {
                    eArg.NewValue = conversionResultTuple.ConvertedInstance;
                }
                InvokeWithBeforeSetValue(eArg, () => patchProperty.Property.ValueProvider.SetValue(eArg.ParentObject,
                    eArg.NewValue));

                return new RemovedPropertyTypeResult(patchProperty.Property.PropertyType, false);
            }
        }
        
        /// <summary>
        /// The "test" operation tests that a value at the target location is
        /// equal to a specified value.
        /// 
        /// The operation object MUST contain a "value" member that conveys the
        /// value to be compared to the target location's value.
        /// 
        /// The target location MUST be equal to the "value" value for the
        /// operation to be considered successful.
        /// 
        /// Here, "equal" means that the value at the target location and the
        /// value conveyed by "value" are of the same JSON type, and that they
        /// are considered equal by the following rules for that type:
        /// 
        /// o  strings: are considered equal if they contain the same number of
        ///    Unicode characters and their code points are byte-by-byte equal.
        /// 
        /// o  numbers: are considered equal if their values are numerically
        ///    equal.
        /// 
        /// o  arrays: are considered equal if they contain the same number of
        ///    values, and if each value can be considered equal to the value at
        ///    the corresponding position in the other array, using this list of
        ///    type-specific rules.
        ///
        /// o  objects: are considered equal if they contain the same number of
        ///    members, and if each member can be considered equal to a member in
        ///    the other object, by comparing their keys (as strings) and their
        ///    values (using this list of type-specific rules).
        ///
        /// o  literals (false, true, and null): are considered equal if they are
        ///    the same.
        ///
        /// Note that the comparison that is done is a logical comparison; e.g.,
        /// whitespace between the member values of an array is not significant.
        ///
        /// Also, note that ordering of the serialization of object members is
        /// not significant.
        /// 
        /// Note that we divert from the rules here - we use .NET's comparison,
        /// not the one above.  In a future version, a "strict" setting might
        /// be added (configurable), that takes into account above rules.
        ///
        /// For example:
        ///
        /// { "op": "test", "path": "/a/b/c", "value": "foo" }
        /// </summary>
        /// <param name="operation">The test operation</param>
        /// <param name="objectApplyTo">Object to apply the operation to</param>
        public void Test(Operation<T> operation, T objectToApplyTo)
        {
            var result = GetValueAtLocation(operation.path, objectToApplyTo, operation);
            object valToTest = operation.value;
            if (result.PropertyValue != null)
            {
                // we have something. Lets see if it is a primitive
                Type retrievedType = result.PropertyValue.GetType();
                if (retrievedType.IsValueType)
                {
                    valToTest = Convert.ChangeType(valToTest, retrievedType);
                }
            }
            if (!object.Equals(valToTest, result.PropertyValue))
            {
                throw new Exception();
            }
                
            
        }
        
        /// <summary>
        /// The "replace" operation replaces the value at the target location
        /// with a new value.  The operation object MUST contain a "value" member
        /// whose content specifies the replacement value.
        /// 
        /// The target location MUST exist for the operation to be successful.
        /// 
        /// For example:
        /// 
        /// { "op": "replace", "path": "/a/b/c", "value": 42 }
        /// 
        /// This operation is functionally identical to a "remove" operation for
        /// a value, followed immediately by an "add" operation at the same
        /// location with the replacement value.
        /// </summary>
        /// <param name="operation">The replace operation</param>
        /// <param name="objectApplyTo">Object to apply the operation to</param>
        public void Replace(Operation<T> operation, T objectToApplyTo)
        {
            var removeResult = Remove(operation.path, objectToApplyTo, operation);

            if (removeResult.HasError)
            {
                // return => currently not applicable, will throw exception in Remove method
            }

            if (!removeResult.HasError && removeResult.ActualType == null)
            {
                // the remove operation completed succesfully, but we could not determine the type.  
                throw new JsonPatchException(
                   new JsonPatchError(
                     objectToApplyTo,
                     operation,
                     string.Format("Patch failed: could not determine type of property at location {0}", operation.path)),
                   422);
            }
            ConversionResult conversionResult;
            var propType = removeResult.ActualType;
            var value = operation.value;
            if (!propType.IsValueType && propType != typeof(string) && value != null && value.GetType() == typeof(string))
            {
                object newObj;
                if (CustomDeserializationHandler != null)
                {
                    newObj = CustomDeserializationHandler(value.ToString(), propType);
                }
                else
                {
                    newObj = JsonConvert.DeserializeObject(value.ToString(), propType);
                }
                conversionResult = new ConversionResult(true, newObj);
            }
            else
            {
                conversionResult = PropertyHelpers.ConvertToActualType(removeResult.ActualType, operation.value);
            }

            if (!conversionResult.CanBeConverted)
            {
                throw new JsonPatchException(
                   new JsonPatchError(
                     objectToApplyTo,
                     operation,
                     string.Format("Patch failed: property value cannot be converted to type of path location {0}", operation.path)),
                   422);
            }

            Add(operation.path, conversionResult.ConvertedInstance, objectToApplyTo, operation);
        }

        /// <summary>
        ///  The "copy" operation copies the value at a specified location to the
        ///  target location.
        ///  
        ///  The operation object MUST contain a "from" member, which is a string
        ///  containing a JSON Pointer value that references the location in the
        ///  target document to copy the value from.
        ///  
        ///  The "from" location MUST exist for the operation to be successful.
        ///  
        ///  For example:
        ///  
        ///  { "op": "copy", "from": "/a/b/c", "path": "/a/b/e" }
        ///  
        ///  This operation is functionally identical to an "add" operation at the
        ///  target location using the value specified in the "from" member.
        /// </summary>
        /// <param name="operation">The copy operation</param>
        /// <param name="objectApplyTo">Object to apply the operation to</param>
        public void Copy(Operation<T> operation, T objectToApplyTo)
        {
            // get value at from location and add that value to the path location
            var valueAtFromLocationResult = GetValueAtLocation(operation.from, objectToApplyTo, operation);

            if (valueAtFromLocationResult.HasError)
            {
                // currently not applicable, will throw exception in GetValueAtLocation method
            }

            Add(operation.path,
                valueAtFromLocationResult.PropertyValue,
                objectToApplyTo,
                operation);
        }

        private GetValueResult GetValueAtLocation(string location, object objectToGetValueFrom, Operation operationToReport)
        {
            // get value from "objectToGetValueFrom" at location "location"
            object valueAtLocation = null;

            var pathResult = PropertyHelpers.GetActualPropertyPath(
                location,
                objectToGetValueFrom,
                operationToReport, false);

            var positionAsInteger = pathResult.NumericEnd;
            var actualFromProperty = pathResult.PathToProperty;

            // first, analyze the tree. 
            var result = new ObjectTreeAnalysisResult(objectToGetValueFrom, actualFromProperty, ContractResolver);

            var patchProperty = result.JsonPatchProperty;

            // is the path an array (but not a string (= char[]))?  In this case,
            // the path must end with "/position" or "/-", which we already determined before. 
            if (positionAsInteger > -1)
            {
                if (PropertyHelpers.IsNonStringList(patchProperty.Property.PropertyType))
                {
                    // now, get the generic type of the enumerable
                    if (patchProperty.Property.Readable)
                    {
                        var array = (IList)patchProperty.Property.ValueProvider
                            .GetValue(patchProperty.Parent);

                        if (positionAsInteger >= array.Count)
                        {
                            throw new JsonPatchException(
                               new JsonPatchError(
                                 objectToGetValueFrom,
                                 operationToReport,
                                 string.Format("Patch failed: property at location from: {0} does not exist", location)),
                               422);
                        }

                        valueAtLocation = array[positionAsInteger];
                    }
                    else
                    {
                        throw new JsonPatchException(
                             new JsonPatchError(
                               objectToGetValueFrom,
                               operationToReport,
                               string.Format("Patch failed: cannot get property at location from from: {0}. Possible cause: the property doesn't have an accessible getter.", location)),
                             422);
                    }
                }
                else
                {
                    throw new JsonPatchException(
                        new JsonPatchError(
                          objectToGetValueFrom,
                          operationToReport,
                           string.Format("Patch failed: provided from path is invalid for array property type at location from: {0}: expected array", location)),
                        422);
                }
            }
            else
            {
                if (!patchProperty.Property.Readable)
                {
                    throw new JsonPatchException(
                            new JsonPatchError(
                              objectToGetValueFrom,
                              operationToReport,
                               string.Format("Patch failed: cannot get property at location from from: {0}. Possible cause: the property doesn't have an accessible getter.", location)),
                            422);
                }
                valueAtLocation = patchProperty.Property.ValueProvider
                             .GetValue(patchProperty.Parent);
            }

            return new GetValueResult(valueAtLocation, false);
        }
    }
}
