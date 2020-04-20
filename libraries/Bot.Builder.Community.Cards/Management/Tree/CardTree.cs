﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Bot.Schema;
using Newtonsoft.Json.Linq;

namespace Bot.Builder.Community.Cards.Management.Tree
{
    internal static class CardTree
    {
        private const string SpecifyManually = " Try specifying the node type manually instead of using null.";

        private static readonly Dictionary<string, TreeNodeType> _cardTypes = new Dictionary<string, TreeNodeType>(StringComparer.OrdinalIgnoreCase)
        {
            { CardConstants.AdaptiveCardContentType, TreeNodeType.AdaptiveCard },
            { AnimationCard.ContentType, TreeNodeType.AnimationCard },
            { AudioCard.ContentType, TreeNodeType.AudioCard },
            { HeroCard.ContentType, TreeNodeType.HeroCard },
            { ReceiptCard.ContentType, TreeNodeType.ReceiptCard },
            { SigninCard.ContentType, TreeNodeType.SigninCard },
            { OAuthCard.ContentType, TreeNodeType.OAuthCard },
            { ThumbnailCard.ContentType, TreeNodeType.ThumbnailCard },
            { VideoCard.ContentType, TreeNodeType.VideoCard },
        };

        private static readonly Dictionary<TreeNodeType, ITreeNode> _tree = new Dictionary<TreeNodeType, ITreeNode>
        {
            {
                TreeNodeType.Batch, new EnumerableTreeNode<IMessageActivity>(TreeNodeType.Activity, DataIdTypes.Batch)
            },
            {
                TreeNodeType.Activity, new TreeNode<IMessageActivity, IEnumerable<Attachment>>((activity, next) =>
                {
                    // The nextAsync return value is not needed here because the Attachments property reference will remain unchanged
                    next(activity.Attachments, TreeNodeType.Carousel);

                    return activity;
                })
            },
            {
                TreeNodeType.Carousel, new EnumerableTreeNode<Attachment>(TreeNodeType.Attachment, DataIdTypes.Carousel)
            },
            {
                TreeNodeType.Attachment, new TreeNode<Attachment, object>((attachment, next) =>
                {
                    var contentType = attachment.ContentType;

                    if (contentType != null && _cardTypes.ContainsKey(contentType))
                    {
                        // The nextAsync return value is needed here because the attachment could be an Adaptive Card
                        // which would mean a new object was generated by the JObject conversion/deconversion
                        attachment.Content = next(attachment.Content, _cardTypes[contentType]);
                    }

                    return attachment;
                })
            },
            {
                TreeNodeType.AdaptiveCard, new TreeNode<object, IEnumerable<JObject>>((card, next) =>
                {
                    // Return the new object after it's been converted to a JObject and back
                    // so that the attachment node can assign it back to the Content property
                    return card.ToJObjectAndBack(
                        cardJObject =>
                        {
                            next(
                                AdaptiveCardUtil.NonDataDescendants(cardJObject)
                                    .Select(token => token is JObject element
                                            && element.GetValue(CardConstants.KeyType) is JToken type
                                            && type.Type == JTokenType.String
                                            && type.ToString().Equals(CardConstants.ActionSubmit)
                                        ? element : null)
                                    .WhereNotNull(), TreeNodeType.SubmitActionList);
                        }, true);
                })
            },
            {
                TreeNodeType.AnimationCard, new RichCardTreeNode<AnimationCard>(card => card.Buttons)
            },
            {
                TreeNodeType.AudioCard, new RichCardTreeNode<AudioCard>(card => card.Buttons)
            },
            {
                TreeNodeType.HeroCard, new RichCardTreeNode<HeroCard>(card => card.Buttons)
            },
            {
                TreeNodeType.OAuthCard, new RichCardTreeNode<OAuthCard>(card => card.Buttons)
            },
            {
                TreeNodeType.ReceiptCard, new RichCardTreeNode<ReceiptCard>(card => card.Buttons)
            },
            {
                TreeNodeType.SigninCard, new RichCardTreeNode<SigninCard>(card => card.Buttons)
            },
            {
                TreeNodeType.ThumbnailCard, new RichCardTreeNode<ThumbnailCard>(card => card.Buttons)
            },
            {
                TreeNodeType.VideoCard, new RichCardTreeNode<VideoCard>(card => card.Buttons)
            },
            {
                TreeNodeType.SubmitActionList, new EnumerableTreeNode<object>(TreeNodeType.SubmitAction, DataIdTypes.Card)
            },
            {
                TreeNodeType.CardActionList, new EnumerableTreeNode<CardAction>(TreeNodeType.CardAction, DataIdTypes.Card)
            },
            {
                TreeNodeType.SubmitAction, new TreeNode<object, JObject>((action, next) =>
                {
                    // If the entry point was the Adaptive Card or higher
                    // then the action will already be a JObject
                    return action.ToJObjectAndBack(
                        actionJObject =>
                        {
                            if (actionJObject.GetValue(CardConstants.KeyData) is JObject data)
                            {
                                next(data, TreeNodeType.ActionData);
                            }
                        }, true);
                })
            },
            {
                TreeNodeType.CardAction, new TreeNode<CardAction, JObject>((action, next, reassignChildren) =>
                {
                    if (action.Type == ActionTypes.MessageBack || action.Type == ActionTypes.PostBack)
                    {
                        if (action.Value.ToJObject(true) is JObject valueJObject)
                        {
                            next(valueJObject, TreeNodeType.ActionData);

                            if (reassignChildren)
                            {
                                action.Value = action.Value.FromJObject(valueJObject);
                            }
                        }
                        else
                        {
                            action.Text = action.Text.ToJObjectAndBack(
                                jObject =>
                                {
                                    next(jObject, TreeNodeType.ActionData);
                                },
                                true);
                        }
                    }

                    return action;
                })
            },
            {
                TreeNodeType.ActionData, new TreeNode<JObject, DataItem>((data, next) =>
                {
                    foreach (var type in DataIdTypes.Collection)
                    {
                        var id = data.GetIdFromActionData(type);

                        if (id != null)
                        {
                            next(new DataItem(type, id), TreeNodeType.Id);
                        }
                    }

                    return data;
                })
            },
            {
                TreeNodeType.Id, new TreeNode<DataItem, object>((id, _) => id)
            },
        };

        /// <summary>
        /// Enters and exits the tree at the specified nodes.
        /// </summary>
        /// <typeparam name="TEntry">The .NET type of the entry node.</typeparam>
        /// <typeparam name="TExit">The .NET type of the exit node.</typeparam>
        /// <param name="entryValue">The entry value.</param>
        /// <param name="action">A delegate to execute on each exit value
        /// that is expected to return that value or a new object.
        /// Note that the argument is guaranteed to be non-null.</param>
        /// <param name="entryType">The explicit position of the entry node in the tree.
        /// If this is null then the position is inferred from the TEntry type parameter.
        /// Note that this parameter is required if the type is <see cref="object"/>
        /// or if the position otherwise cannot be unambiguously inferred from the type.</param>
        /// <param name="exitType">The explicit position of the exit node in the tree.
        /// If this is null then the position is inferred from the TExit type parameter.
        /// Note that this parameter is required if the type is <see cref="object"/>
        /// or if the position otherwise cannot be unambiguously inferred from the type.</param>
        /// <param name="reassignChildren">True if each child should be reassigned to its parent during recursion
        /// (which breaks Adaptive Card attachment content references when they get converted to a
        /// <see cref="JObject"/> and back), false if each original reference should remain.</param>
        /// <param name="processIntermediateValue">A delegate to execute on each node during recursion.</param>
        /// <returns>The possibly-modified entry value. This is needed if a new object was created
        /// to modify the value, such as when an Adaptive Card is converted to a <see cref="JObject"/>.</returns>
        internal static TEntry Recurse<TEntry, TExit>(
                TEntry entryValue,
                Action<TExit> action,
                TreeNodeType? entryType = null,
                TreeNodeType? exitType = null,
                bool reassignChildren = false,
                Action<object, ITreeNode> processIntermediateValue = null)
            where TEntry : class
            where TExit : class
        {
            ITreeNode entryNode = null;
            ITreeNode exitNode = null;

            try
            {
                entryNode = GetNode<TEntry>(entryType);
            }
            catch (Exception ex)
            {
                throw GetNodeArgumentException<TEntry>(ex);
            }

            try
            {
                exitNode = GetNode<TExit>(exitType);
            }
            catch (Exception ex)
            {
                throw GetNodeArgumentException<TExit>(ex, "exit");
            }

            object Next(object child, TreeNodeType childType)
            {
                var childNode = _tree[childType];
                var modifiedChild = child;

                if (childNode == exitNode)
                {
                    if (GetExitValue<TExit>(child) is TExit typedChild)
                    {
                        action(typedChild);
                    }
                }
                else
                {
                    processIntermediateValue?.Invoke(child, childNode);

                    // CallChildAsync will be executed immediately even though it's not awaited
                    modifiedChild = childNode.CallChild(child, Next, reassignChildren);
                }

                return reassignChildren ? modifiedChild : child;
            }

            processIntermediateValue?.Invoke(entryValue, entryNode);

            return entryNode.CallChild(entryValue, Next, reassignChildren) as TEntry;
        }

        internal static void ApplyIds<TEntry>(TEntry entryValue, DataIdOptions options = null, TreeNodeType? entryType = null)
            where TEntry : class
        {
            options = options ?? new DataIdOptions(DataIdTypes.Action);

            var modifiedOptions = options.Clone();

            Recurse(
                entryValue,
                (JObject data) =>
                {
                    data.ApplyIdsToActionData(modifiedOptions);
                },
                entryType,
                TreeNodeType.ActionData,
                true,
                (value, node) =>
                {
                    if (node == _tree[TreeNodeType.SubmitAction])
                    {
                        // We need to create a "data" object in the submit action
                        // if there isn't one already
                        value = value.ToJObjectAndBack(submitAction =>
                        {
                            if (submitAction.GetValue(CardConstants.KeyData).IsNullish())
                            {
                                submitAction.SetValue(CardConstants.KeyData, new JObject());
                            }
                        });
                    }

                    if (node.IdType is string idType)
                    {
                        if (options.HasIdType(idType))
                        {
                            var id = options.Get(idType);

                            if (id is null)
                            {
                                modifiedOptions.Set(idType, DataIdTypes.GenerateId(idType));
                            }
                        }
                    }
                });
        }

        internal static ISet<DataItem> GetIds<TEntry>(TEntry entryValue, TreeNodeType? entryType = null)
            where TEntry : class
        {
            var ids = new HashSet<DataItem>();

            Recurse(
                entryValue,
                (DataItem dataId) =>
                {
                    ids.Add(dataId);
                }, entryType);

            return ids;
        }

        private static TExit GetExitValue<TExit>(object child)
                where TExit : class
            => child is JToken jToken && !typeof(JToken).IsAssignableFrom(typeof(TExit)) ? jToken.ToObject<TExit>() : child as TExit;

        private static ITreeNode GetNode<T>(TreeNodeType? nodeType)
        {
            var t = typeof(T);

            if (nodeType is null)
            {
                if (t == typeof(object))
                {
                    throw new Exception("A node cannot be automatically determined from a System.Object type argument." + SpecifyManually);
                }

                var matchingNodes = new List<ITreeNode>();

                foreach (var possibleNode in _tree.Values)
                {
                    var possibleNodeTValue = possibleNode.GetTValue();

                    if (possibleNodeTValue.IsAssignableFrom(t) && possibleNodeTValue != typeof(object) && possibleNodeTValue != typeof(IEnumerable<object>))
                    {
                        matchingNodes.Add(possibleNode);
                    }
                }

                var count = matchingNodes.Count();

                if (count < 1)
                {
                    throw new Exception($"No node exists that's assignable from the type argument: {t}. Try using a different type.");
                }

                if (count > 1)
                {
                    throw new Exception($"Multiple nodes exist that are assignable from the type argument: {t}." + SpecifyManually);
                }

                return matchingNodes.First();
            }

            var exactNode = _tree[nodeType.Value];

            return exactNode.GetTValue().IsAssignableFrom(t)
                ? exactNode
                : throw new Exception($"The node type {nodeType} is not assignable from the type argument: {t}."
                    + " Make sure you're providing the correct node type.");
        }

        private static ArgumentException GetNodeArgumentException<TEntry>(Exception inner, string entryOrExit = "entry")
        {
            return new ArgumentException(
                $"The {entryOrExit} node could not be determined from the type argument: {typeof(TEntry)}.",
                $"{entryOrExit}Type",
                inner);
        }
    }
}
