﻿/*
 * Copyright 2021 faddenSoft
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;


namespace CommonUtil {
    /// <summary>
    /// Map file offsets to 65xx addresses and vice-versa.  Supports multiple regions
    /// with overlapping address ranges.
    /// </summary>
    /// <remarks>
    /// The basic structure is a list of regions, identified by start offset and length, that
    /// specify the memory address.
    ///
    /// This gets complicated because it's possible to have multiple regions that are assembled
    /// to occupy the same address range (because of overlays or bank-switching).  Some regions
    /// may be nested inside other regions.  A reference to a given address could potentially
    /// resolve to multiple offsets.  Any address-to-offset lookup will need to take into
    /// account the location of the reference, so that references can be resolved in the region
    /// with the appropriate scope.
    ///
    /// There are three basic API modes:
    /// (1) Structural.  Add, modify, and remove regions.  Needed by the "edit region" dialog.
    ///     This matches exactly with the contents of the project file.
    /// (2) Hierarchical.  Used when converting an offset to an address, which can't be
    ///     accomplished with a simple map because we need to take into account the offset of
    ///     the reference.  The tree best represents the relationship between regions.
    /// (3) Linear.  When generating assembly sources or the display list, we need to identify
    ///     the lines that have an address change event (even if the address doesn't change).
    ///     This will be done as we walk through the code.  For easy interaction with an
    ///     iterator, we flatten it out.
    ///
    /// These are different enough that it's best to use three different data structures.  The
    /// list of regions is the primary structure, and the other two are generated from it.  Changes
    /// to the map are very infrequent, and analyzing the file contents may hit the map
    /// frequently, so we want to optimize for "read" accesses.
    ///
    /// A region can be uniquely identified by {offset,length}.  There can be multiple regions
    /// starting at a given offset, or ending at a given offset, but we disallow regions that
    /// are 100% overlapping.  This assertion is complicated slightly by the existence of
    /// regions with "floating" end points.
    ///
    /// It is valid for parts of the file to have no address mapping.  This is useful for things
    /// like system file headers that are part of the file but wouldn't be part of the source
    /// code (such as the C64 PRG address header), or data not addressable by the 6502 (such as
    /// the CHR graphics block in NES programs).  The most significant impact this has on
    /// SourceGen is that we never resolve address-to-offset lookups in such a region.
    ///
    /// For design notes, see https://github.com/fadden/6502bench/issues/107
    /// </remarks>
    public class AddressMap : IEnumerable<AddressMap.AddressMapEntry> {
        private const int OFFSET_MAX = (1 << 24) - 1;   // max valid offset (16MB file)
        private const int ADDR_MAX = (1 << 24) - 1;     // max valid addr (24-bit address space)

        /// <summary>
        /// Length value to use for regions with a floating end point.
        /// </summary>
        public const int FLOATING_LEN = -1024;

        /// <summary>
        /// Address value to use for non-addressable regions of the file, such as file headers
        /// stripped by the system loader or chunks loaded into non-addressable memory.
        /// </summary>
        public const int NON_ADDR = -1025;

        #region Structural

        /// <summary>
        /// Code starting at the specified offset will have the specified address.
        ///
        /// The entries are held in the list in order, sorted primarily by increasing start offset,
        /// secondarily by decreasing end offset.  If there are multiple regions at the same
        /// offset, the larger (parent) region will appear first (convenient if you're pushing
        /// things onto a stack as you traverse the list).
        ///
        /// It is valid for the map to be completely empty, or for there to be ranges of offsets
        /// for which there is no entry.
        ///
        /// Instances are immutable.
        /// </summary>
        [Serializable]
        public class AddressMapEntry {
            // Offset at which region starts.
            public int Offset { get; private set; }
            // Length of region; invalid for a "floating" end point.
            public int Length { get; private set; }
            // Address to map start of region to.
            public int Address { get; private set; }
            // Is the end point floating?
            public bool IsFloating { get; private set; }
            // Should we try to generate this with a relative .ORG statement?  (This is strictly
            // for code generation, and has no effect on anything here.)
            public bool IsRelative { get; private set; }

            // THOUGHT: we need to be able to do certain info queries from edit dialog.  We
            // can uniquely identify a node in the region list by offset/len, but that gets
            // funny with floating lengths.  We can use a "was floating" flag here to make
            // it possible to match the offset/float_len to a node in the hierarchical tree.
            // (add another constructor)

            public AddressMapEntry(int offset, int len, int addr, bool isFloating,
                    bool isRelative) {
                Offset = offset;
                Length = len;
                Address = addr;
                IsFloating = isFloating;
                IsRelative = isRelative;
            }
            public override string ToString() {
                return "[AddrMapEnt: +" + Offset.ToString("x6") + " len=$" + Length.ToString("x4") +
                    " addr=$" + Address.ToString("x4") + " isFloat=" + IsFloating +
                    " isRel=" + IsRelative + "]";
            }
        }

        /// <summary>
        /// Total length, in bytes, of the file spanned by this map.
        /// </summary>
        private int mSpanLength;

        /// <summary>
        /// List of definitions, in sorted order.
        /// </summary>
        private List<AddressMapEntry> mRegionList = new List<AddressMapEntry>();


        /// <summary>
        /// Constructor.  Creates an empty map.
        /// </summary>
        /// <param name="length">Total length, in bytes, of the file spanned by this map.</param>
        public AddressMap(int length) {
            mSpanLength = length;
            Regenerate();
        }

        /// <summary>
        /// Constructor.  Creates a map from a list of entries.
        /// </summary>
        /// <param name="length">Total length, in bytes, of the file spanned by this map.</param>
        /// <param name="entries">List of AddressMapEntry.</param>
        public AddressMap(int length, List<AddressMapEntry> entries) {
            mSpanLength = length;

            // Add entries one at a time, rather than just cloning the list, to ensure correctness.
            // (Shouldn't be necessary since we're only doing this to pass the address map to
            // plugins, but... better safe.)
            foreach (AddressMapEntry ent in entries) {
                AddResult result = AddRegion(ent.Offset, ent.Length, ent.Address, ent.IsRelative);
                if (result != AddResult.Okay) {
                    throw new Exception("Unable to add entry (" + result + "): " + ent);
                }
            }
            Debug.Assert(entries.Count == mRegionList.Count);
            Regenerate();
        }

        public void Clear() {
            mRegionList.Clear();
            Regenerate();
        }

        /// <summary>
        /// Generates a copy of the list of entries, suitable for passing to a constructor.
        /// </summary>
        /// <param name="spanLength">Receives the map's span length.</param>
        /// <returns>Copy of list.</returns>
        public List<AddressMapEntry> GetEntryList(out int spanLength) {
            List<AddressMapEntry> newList = new List<AddressMapEntry>(mRegionList.Count);
            foreach (AddressMapEntry ent in mRegionList) {
                newList.Add(ent);
            }
            spanLength = mSpanLength;
            return newList;
        }

        // IEnumerable
        public IEnumerator<AddressMapEntry> GetEnumerator() {
            return ((IEnumerable<AddressMapEntry>)mRegionList).GetEnumerator();
        }

        // IEnumerable
        IEnumerator IEnumerable.GetEnumerator() {
            return ((IEnumerable<AddressMapEntry>)mRegionList).GetEnumerator();
        }

        /// <summary>
        /// Number of entries in the address map.
        /// </summary>
        public int RegionCount { get { return mRegionList.Count; } }

        /// <summary>
        /// Error codes for AddRegion().
        /// </summary>
        public enum AddResult {
            Unknown = 0,
            Okay,               // success!
            InvalidValue,       // offset, length, or address parameter is invalid
            OverlapExisting,    // new region overlaps existing region exactly
            OverlapFloating,    // new start matches existing; one or both are floating
            StraddleExisting,   // new region straddles one or more existing regions
        };

        /// <summary>
        /// Validate offset/length/addr arguments.
        /// </summary>
        /// <remarks>
        /// We need to verify:
        /// - offset &gt;= 0
        /// - offset &lt; total length of file
        /// - either length is floating, or:
        ///   - length > 0
        ///   - length &lt; total length of file
        ///   - offset + length &lt; total length of file
        /// - either address is NON_ADDR, or:
        ///   - addr &gt; 0
        ///   - addr &lt;= ADDR_MAX
        /// </remarks>
        /// <returns>True if everything looks good.</returns>
        private bool ValidateArgs(int offset, int length, int addr) {
            return offset >= 0 && offset < mSpanLength &&
                (length != FLOATING_LEN ? offset + length <= mSpanLength : true) &&
                ((length > 0 && length <= mSpanLength) || length == FLOATING_LEN) &&
                ((addr >= 0 && addr <= ADDR_MAX) || addr == NON_ADDR);
        }

        /// <summary>
        /// Adds a new region.
        /// </summary>
        /// <param name="offset">File offset of region start.</param>
        /// <param name="length">Length of region, or -1 for a floating end point.</param>
        /// <param name="addr">Address of region start.</param>
        /// <param name="isRelative">True if code generator should output relative
        ///   assembler directive operand.</param>
        /// <returns>Failure code.</returns>
        public AddResult AddRegion(int offset, int length, int addr, bool isRelative) {
            if (!ValidateArgs(offset, length, addr)) {
                Debug.WriteLine("AddRegion: invalid arg");
                return AddResult.InvalidValue;
            }

            AddressMapEntry newEntry = new AddressMapEntry(offset, length, addr,
                length == FLOATING_LEN, isRelative);

            // Empty list?
            if (mRegionList.Count == 0) {
                mRegionList.Add(newEntry);
                Regenerate();
                return AddResult.Okay;
            }

            // Find insertion point.
            int insIdx;
            for (insIdx = 0; insIdx < mRegionList.Count; insIdx++) {
                AddressMapEntry ent = mRegionList[insIdx];
                if (ent.Offset > offset) {
                    // Insert before this one.
                    break;
                } else if (ent.Offset == offset) {
                    // We share a start point with this entry.  See if we fit inside it or
                    // wrap around it.
                    if (length == FLOATING_LEN || ent.Length == FLOATING_LEN) {
                        // Can't share a start point with a variable-length region.
                        return AddResult.OverlapFloating;
                    } else if (ent.Length == length) {
                        // Same offset/length as existing entry.
                        return AddResult.OverlapExisting;
                    } else if (ent.Length < length) {
                        // New region is larger, would become parent, so insert before this.
                        break;
                    } else {
                        // New region is smaller and will be a child of this entry, so we want
                        // to insert *after* this point.  Loop again to see if the following
                        // entry is also a parent for this new one.
                        Debug.Assert(ent.Length > length);
                    }
                }
            }

            // The insertion index indicates the entry we want to insert before.  We need to
            // confirm that the new block doesn't straddle the blocks on either side.  If we're
            // inserting into a bunch of blocks with coincident start points, it's possible for
            // the blocks appearing before and after to share the same start offset.

            if (insIdx > 0) {
                // Check previous block.  We know that its offset is <= the new offset, so
                // either its a parent or a sibling.
                AddressMapEntry ent = mRegionList[insIdx - 1];
                if (ent.Offset == offset) {
                    // Previous is our parent.  These things were checked earlier.
                    Debug.Assert(length != FLOATING_LEN && ent.Length != FLOATING_LEN);
                    Debug.Assert(ent.Length > length);
                } else {
                    // Existing block starts before this new one.  The existing block must either
                    // be floating, be completely before this, or completely envelop this.
                    Debug.Assert(ent.Offset < offset);
                    if (ent.Length == FLOATING_LEN) {
                        // sibling -- must end before us
                    } else if (ent.Offset + ent.Length <= offset) {
                        // sibling
                    } else if (length == FLOATING_LEN) {
                        // existing is parent, we stop at their end
                    } else if (ent.Offset + ent.Length >= offset + length) {
                        // existing is parent, ending at or after our end
                    } else {
                        // whoops
                        return AddResult.StraddleExisting;
                    }
                }
            }
            if (insIdx < mRegionList.Count) {
                // Check following block.  We know that its offset is >= the new offset, so it's
                // either a child or a sibling.
                AddressMapEntry ent = mRegionList[insIdx];
                if (ent.Offset == offset) {
                    // Following block is our child.  These things were checked earlier.
                    Debug.Assert(length != FLOATING_LEN && ent.Length != FLOATING_LEN);
                    Debug.Assert(ent.Length < length);
                } else {
                    // Existing block starts after this new one.  The existing block must either
                    // be floating, be completely after this, or be completely enveloped by this.
                    Debug.Assert(ent.Offset > offset);
                    if (ent.Length == FLOATING_LEN) {
                        // child or sibling, depending on start offset
                    } else if (offset + length <= ent.Offset) {
                        // sibling
                    } else if (length == FLOATING_LEN) {
                        // sibling
                    } else if (ent.Offset + ent.Length <= offset + length) {
                        // existing is child, ending at or before our end
                    } else {
                        // whoops
                        return AddResult.StraddleExisting;
                    }
                }
            }

            mRegionList.Insert(insIdx, newEntry);
            Regenerate();
            return AddResult.Okay;
        }

        /// <summary>
        /// Edits the region with the specified offset/len, changing the values of addr and isRel.
        /// </summary>
        /// <param name="offset">Offset of region to edit.</param>
        /// <param name="length">Length of region to edit.</param>
        /// <param name="addr">New value for address.</param>
        /// <param name="isRelative">New value for IsRelative.</param>
        /// <returns>True if a region was edited, false otherwise.</returns>
        public bool EditRegion(int offset, int length, int addr, bool isRelative) {
            if (!ValidateArgs(offset, length, addr)) {
                throw new Exception("Bad EditRegion args +" + offset.ToString("x6") +
                    " " + length + " $" + addr);
            }

            int idx = FindRegion(offset, length);
            if (idx < 0) {
                return false;
            }
            mRegionList[idx] = new AddressMapEntry(offset, length, addr, length == FLOATING_LEN,
                    isRelative);
            Regenerate();
            return true;
        }

        /// <summary>
        /// Removes the region with the specified offset/len.
        /// </summary>
        /// <param name="offset">Offset of region to remove.</param>
        /// <param name="length">Length of region to remove.</param>
        /// <returns>True if a region was removed, false otherwise.</returns>
        public bool RemoveRegion(int offset, int length) {
            if (!ValidateArgs(offset, length, 0)) {
                throw new Exception("Bad RemoveRegion args +" + offset.ToString("x6") +
                    " " + length);
            }

            int idx = FindRegion(offset, length);
            if (idx < 0) {
                return false;
            }
            mRegionList.RemoveAt(idx);
            Regenerate();
            return true;
        }

        /// <summary>
        /// Finds a region with a matching offset and length.
        /// </summary>
        /// <param name="offset">Offset to match.</param>
        /// <param name="length">Length to match (may be FLOATING_LEN).</param>
        /// <returns>Index of matching region, or -1 if not found.</returns>
        private int FindRegion(int offset, int length) {
            for (int i = 0; i < mRegionList.Count; i++) {
                if (mRegionList[i].Offset == offset && mRegionList[i].Length == length) {
                    return i;
                }
            }
            return -1;
        }

        // Returns true if adding the specified region is a valid action.
        // ??? do we want to do this, or just ask "does region exist"?  Depends on
        // flow in edit dialog.
        //public bool CanAddRegion(int offset, int length) {
        //    return false;
        //}

        /// <summary>
        /// Gets the first region with the specified offset and length.
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        //public AddressMapEntry GetFirstRegion(int offset, int length) {
        //    int idx = FindRegion(offset, length);
        //    if (idx < 0) {
        //        return null;
        //    } else {
        //        return mRegionList[idx];
        //    }
        //}

        /// <summary>
        /// Gets a list of the regions with the specified offset value.
        /// </summary>
        /// <param name="offset">File offset.</param>
        /// <returns>List of entries; may be empty.</returns>
        public List<AddressMapEntry> GetRegions(int offset) {
            List<AddressMapEntry> regions = new List<AddressMapEntry>();
            for (int i = 0; i < mRegionList.Count; i++) {
                if (mRegionList[i].Offset == offset) {
                    regions.Add(mRegionList[i]);
                }
                if (mRegionList[i].Offset > offset) {
                    // Regions are in sorted order, we're done.
                    break;
                }
            }
            return regions;
        }

        /// <summary>
        /// Regenerates sub-structures after every change.
        /// </summary>
        private void Regenerate() {
            GenerateTree();
            GenerateLinear();
            Debug.Assert(DebugValidate());
        }

        /// <summary>
        /// Performs internal consistency checks.  Prints a message and returns false on failure.
        /// </summary>
        private bool DebugValidate() {
            bool result = true;
            result &= DebugValidateStructural();
            result &= DebugValidateHierarchical();
            return result;
        }

        private bool DebugValidateStructural() {
            int lastStart = -1;
            int lastLength = OFFSET_MAX + 1;
            for (int i = 0; i < mRegionList.Count; i++) {
                AddressMapEntry ent = mRegionList[i];

                // Do basic range checks on arguments.
                if (ent.Offset < 0 || ent.Offset > OFFSET_MAX) {
                    Debug.WriteLine("Bad offset +" + ent.Offset.ToString("x6"));
                    return false;
                }
                if (ent.Length <= 0 && ent.Length != FLOATING_LEN) {
                    Debug.WriteLine("Bad length " + ent.Length);
                    return false;
                }
                if (ent.Length > OFFSET_MAX || (long)ent.Offset + (long)ent.Length > OFFSET_MAX) {
                    Debug.WriteLine("Bad length +" + ent.Offset.ToString("x6") +
                        " len=" + ent.Length);
                    return false;
                }
                if ((ent.Address < 0 && ent.Address != NON_ADDR) || ent.Address > ADDR_MAX) {
                    Debug.WriteLine("Bad address $" + ent.Address.ToString("x4"));
                    return false;
                }

                // Compare to EOF.
                if (ent.Length != FLOATING_LEN && ent.Offset + ent.Length > mSpanLength) {
                    Debug.WriteLine("Entry exceeds file bounds");
                    return false;
                }

                // Verify ordering.
                if (ent.Offset < lastStart) {
                    Debug.WriteLine("Bad sort: start");
                    return false;
                } else if (ent.Offset == lastStart) {
                    if (ent.Length == FLOATING_LEN || lastLength == FLOATING_LEN) {
                        Debug.WriteLine("Overlapping float and non-float");
                        return false;
                    }
                    if (ent.Length == lastLength) {
                        Debug.WriteLine("Overlapping regions");
                        return false;
                    } else if (ent.Length > lastLength) {
                        Debug.WriteLine("Bad sort: end");
                        return false;
                    }
                }
                lastStart = ent.Offset;
                lastLength = ent.Length;
            }
            return true;
        }

        public override string ToString() {
            return "[AddressMap: " + mRegionList.Count + " entries]";
        }

        #endregion Structural

        #region Hierarchical

        /// <summary>
        /// Tree data structure.  Only visible internally.
        /// </summary>
        /// <remarks>
        /// Modifications are rare and trees are expected to be small, so the entire tree is
        /// reconstructed whenever a change is made.
        ///
        /// We can reference the AddressMapEntry objects from the structural list unless it
        /// has a floating length.  If it does, we create a new one with the actual length.
        /// </remarks>
        private class TreeNode {
            public AddressMapEntry Entry { get; set; }
            public TreeNode Parent { get; set; }
            public List<TreeNode> Children { get; set; }

            public TreeNode(AddressMapEntry entry, TreeNode parent) {
                Entry = entry;
                Parent = parent;
                // all other fields null/false
            }
        }

        /// <summary>
        /// Top of the hierarchy.  The topmost node is a no-address node that spans the entire
        /// file.
        /// </summary>
        private TreeNode mTopNode;


        /// <summary>
        /// Generates a tree that spans the entire region.
        /// </summary>
        private void GenerateTree() {
            // Create a "fake" node that spans the file, so that any region not covered
            // explicitly is caught here.  It also avoids the need to special-case the top
            // part of the file.
            AddressMapEntry globalEnt = new AddressMapEntry(0, mSpanLength, NON_ADDR, false, false);
            TreeNode topNode = new TreeNode(globalEnt, null);

            // Generate the children of this node.
            int index = -1;
            GenerateChildren(topNode, ref index);

            if (index != mRegionList.Count) {
                Debug.Assert(false, "Not all regions traversed");
            }

            // Replace previous tree.
            mTopNode = topNode;
        }

        /// <summary>
        /// Generates a tree node for the specified region.  This might be a single item, or
        /// the top of a tree.
        /// </summary>
        /// <param name="parent">Parent of this node.  May be null for top-level entries.</param>
        /// <param name="index">On entry, index of current (parent) node.  On exit, index of
        ///   region that is past the tree spanned by this node.</param>
        /// <returns>Newly-created node.</returns>
        private void GenerateChildren(TreeNode parent, ref int index) {
            List<TreeNode> children = new List<TreeNode>();

            index++;
            while (index < mRegionList.Count) {
                AddressMapEntry childEnt = mRegionList[index];

                if (childEnt.Offset >= parent.Entry.Offset + parent.Entry.Length) {
                    // Starts after end of parent, not a child.
                    break;
                }

                if (childEnt.Length == FLOATING_LEN) {
                    // Compute actual length.  We stop at the end of the parent, or at the start
                    // of the following region, whichever comes first.
                    //
                    // Regions with floating ends can't have children, so we don't need to
                    // check for sub-regions.
                    int nextStart = parent.Entry.Offset + parent.Entry.Length;
                    index++;
                    if (index < mRegionList.Count) {
                        // Check next sibling.
                        int sibStart = mRegionList[index].Offset;
                        if (sibStart < nextStart) {
                            nextStart = sibStart;
                        }
                    }
                    AddressMapEntry fixedEnt = new AddressMapEntry(childEnt.Offset,
                        nextStart - childEnt.Offset, childEnt.Address, true, childEnt.IsRelative);
                    children.Add(new TreeNode(fixedEnt, parent));

                    // "index" now points to entry past the child we just added.
                } else {
                    // Add this region to the list, and check for descendants.
                    TreeNode thisNode = new TreeNode(childEnt, parent);
                    children.Add(thisNode);

                    // Check for grandchildren.  "index" will point to first entry beyond this
                    // child and its descendants.
                    GenerateChildren(thisNode, ref index);
                }
            }

            // Set child list if it's non-empty.
            if (children.Count > 0) {
                parent.Children = children;
            }
        }

        /*
        Thoughts on AddressToOffset optimization...

        We can create a simple linear range map, but we have to do it separately for
        every node in the tree (i.e. every unique srcOffset).  We can do this on demand.

        The idea would be to find the leaf node for the source offset, add the address
        range for that node, and then expand outward as we would do when attempting to
        resolve an address.  As we traverse each node we add the address ranges to the
        set, but we don't replace existing entries.  (In some cases a single entry may
        generate multiple disjoint ranges if it overlaps several things.)

        Once the map is generated, we store a reference to it in the tree node, and then
        use that for all future lookups.  Since changes to the tree are rare, and we only
        generate these tables on the first series of lookups after a change, the overhead
        of generating these should be small.  Since it's a list of address ranges
        (similar in principle to TypedRangeSet), it shouldn't be very large, even for
        larger address spaces.
        */

        /// <summary>
        /// Determines the file offset that best contains the specified target address.
        /// </summary>
        /// <remarks>
        /// Algorithm:
        /// - Start in the node that contains the source offset.
        /// - Loop:
        ///   - Recursively scan all children of the current node, in order of increasing Offset.
        ///   - Check the current node.  If it matches, we're done.
        ///   - Move up to the parent.
        /// - If we run off the top of the tree, return -1.
        ///
        /// We're doing a depth-first search, checking the children before the current node.
        ///
        /// Because each node holds an arbitrary address range, we need to search all of them.
        /// There is no early-exit for the not-found case.
        ///
        /// We can't simply compare the Address/Length values to check for a match, because
        /// children may have created "holes".  If the address falls in a node's range, we need
        /// to walk the child list and see if the address is present.
        /// </remarks>
        /// <param name="srcOffset">Offset of the address reference.</param>
        /// <param name="targetAddr">Address to look up.</param>
        /// <returns>The file offset, or -1 if the address falls outside the file.</returns>
        public int AddressToOffset(int srcOffset, int targetAddr) {
            TreeNode startNode = OffsetToNode(srcOffset, mTopNode);

            TreeNode ignoreNode = null;
            while (true) {
                int offset = FindAddress(startNode, ignoreNode, targetAddr);
                if (offset >= 0) {
                    // Return the offset we found.
                    return offset;
                }

                // Didn't find it.  Move up one level, but ignore the branch we've already checked.
                ignoreNode = startNode;
                startNode = startNode.Parent;
                if (startNode == null) {
                    return -1;
                }
            }
        }

        /// <summary>
        /// Finds a matching address range, starting from a specific point in the tree and
        /// searching downward.  One child can be ignored.
        /// </summary>
        /// <param name="node">Start point.</param>
        /// <param name="ignore">Child to ignore (because it was examined earlier).</param>
        /// <param name="targetAddr">Address to find.</param>
        /// <returns>Offset, or -1 if not found.</returns>
        private int FindAddress(TreeNode node, TreeNode ignore, int targetAddr) {
            if (node.Children != null) {
                foreach (TreeNode childNode in node.Children) {
                    if (childNode == ignore) {
                        continue;
                    }
                    int offset = FindAddress(childNode, null, targetAddr);
                    if (offset >= 0) {
                        // Found match in child, return that.
                        return offset;
                    }
                }
            }

            // Wasn't in any of the children, see if it's in this node.
            AddressMapEntry ent = node.Entry;
            if (ent.Address == NON_ADDR) {
                // Non-addressable space.
                return -1;
            }
            if (targetAddr < ent.Address || targetAddr >= ent.Address + ent.Length) {
                // Outside our range of addresses, return failure.
                return -1;
            }

            // We span the correct range of addresses.  See if the requested address
            // falls into a hole spanned by a child.
            if (node.Children != null) {
                int subPosn = targetAddr - ent.Address;     // position of target inside node
                foreach (TreeNode childNode in node.Children) {
                    AddressMapEntry childEnt = childNode.Entry;
                    int childStartPosn = childEnt.Offset - ent.Offset;
                    int childEndPosn = childStartPosn + childEnt.Length;

                    if (childStartPosn > subPosn) {
                        // Child is past the target, it's not in a hole; no need to check
                        // additional children because the children are sorted by Offset.
                        break;
                    } else if (subPosn >= childStartPosn && subPosn < childEndPosn) {
                        // Target is in a hole occupied by the child.  No good.
                        return -1;
                    }
                }
            }
            return ent.Offset + (targetAddr - ent.Address);
        }

        /// <summary>
        /// Converts a file offset to an address.
        /// </summary>
        /// <param name="offset">File offset.</param>
        /// <returns>24-bit address, which may be NON_ADDR.</returns>
        public int OffsetToAddress(int offset) {
            if (offset < 0 || offset >= mSpanLength) {
                // Invalid offset.  Could throw or return an error.
                Debug.WriteLine("Warning: OffsetToAddress invalid offset +" +
                    offset.ToString("x6"));
                return NON_ADDR;
            }

            // Scan tree to find appropriate node.  The tree is guaranteed to cover all offsets.
            TreeNode node = OffsetToNode(offset, mTopNode);

            // Calculate address in this node.
            int ourAddr = NON_ADDR;
            if (node.Entry.Address != NON_ADDR) {
                ourAddr = node.Entry.Address + (offset - node.Entry.Offset);
                Debug.Assert(ourAddr < node.Entry.Address + node.Entry.Length);
            }
            return ourAddr;
        }

        /// <summary>
        /// Recursively descends into the tree to find the node that contains the offset.
        /// </summary>
        /// <param name="offset">File offset.</param>
        /// <param name="node">Node to examine.</param>
        /// <returns>Matching node.</returns>
        private TreeNode OffsetToNode(int offset, TreeNode node) {
            if (node.Children != null) {
                foreach (TreeNode child in node.Children) {
                    AddressMapEntry childEnt = child.Entry;
                    if (offset >= childEnt.Offset && offset < childEnt.Offset + childEnt.Length) {
                        // It's in or below this child.  Check it with tail recursion.
                        return OffsetToNode(offset, child);
                    }
                }
            }
            return node;
        }

        /// <summary>
        /// Checks to see if the specified range of offsets is in an uninterrupted address
        /// range.  Use this to see if something crosses an address-change boundary.  This
        /// does not smooth over no-op address changes.
        /// </summary>
        /// <remarks>
        /// This is NOT intended to say whether the sequence of addresses has a hiccup.  The goal
        /// is to identify multi-byte elements that have a .ORG statement in the middle.
        ///
        /// We can do this in a couple of different ways:
        /// 1. Find the node that holds the offset, confirm that it spans offset+length, and
        ///    then check to see if there are any children that start between the two.
        /// 2. Walk through the linear list and see if there are any events between offset
        ///    and offset+length.
        /// Walking the linear list is simpler but likely slower.
        /// </remarks>
        /// <param name="offset">Start offset.</param>
        /// <param name="length">Length of region.</param>
        /// <returns>True if the range of offsets is unbroken.</returns>
        public bool IsRangeUnbroken(int offset, int length) {
            if (!ValidateArgs(offset, length, 0)) {
                Debug.Assert(false, "Invalid args to IsUnbrokenRange");
                return true;    // most ranges are unbroken, so just go with that
            }

            TreeNode node = OffsetToNode(offset, mTopNode);
            AddressMapEntry ent = node.Entry;
            Debug.Assert(offset >= ent.Offset && offset < ent.Offset + ent.Length);
            int lastOffset = offset + length - 1;   // offset of last byte in range
            if (lastOffset >= ent.Offset + ent.Length) {
                // end of region is not in this node
                return false;
            }

            // The specified range fits inside this node.  See if it's interrupted by a child.
            if (node.Children != null) {
                foreach (TreeNode childNode in node.Children) {
                    AddressMapEntry childEnt = childNode.Entry;

                    if (childEnt.Offset > lastOffset) {
                        // Child is past the target, so range is not in a hole; no need to check
                        // additional children because the children are sorted by Offset.
                        break;
                    } else if (offset <= childEnt.Offset + childEnt.Length - 1 &&
                            lastOffset >= childEnt.Offset) {
                        // Target is in a hole occupied by the child.  No good.
                        return false;
                    }
                }
            }

            return true;
        }

        private bool DebugValidateHierarchical() {
            if (mTopNode.Entry.Offset != 0 || mTopNode.Entry.Length != mSpanLength) {
                Debug.WriteLine("Malformed top node");
                return false;
            }

            int nodeCount = 0;
            if (mTopNode.Children != null) {
                DebugValidateHierarchy(mTopNode.Children, 0, mSpanLength, ref nodeCount);
            }

            // Check node count.  It should have one entry for every entry in the region list
            // (we don't count mTopNode).
            if (nodeCount != mRegionList.Count) {
                Debug.WriteLine("Hierarchical is missing entries: nodeCount=" + nodeCount +
                    " regionCount=" + mRegionList.Count);
                return false;
            }
            return true;
        }

        private bool DebugValidateHierarchy(List<TreeNode> nodeList, int startOffset,
                    int nextOffset, ref int nodeCount) {
            foreach (TreeNode node in nodeList) {
                Debug.Assert(node.Entry.Length >= 0);   // no floaters

                nodeCount++;

                if (node.Entry.Offset < startOffset ||
                        node.Entry.Offset + node.Entry.Length > nextOffset) {
                    Debug.WriteLine("Child node did not fit in parent bounds");
                    return false;
                }
                if (node.Children != null) {
                    // Descend recursively.
                    if (!DebugValidateHierarchy(node.Children, node.Entry.Offset,
                            node.Entry.Offset + node.Entry.Length, ref nodeCount)) {
                        return false;
                    }
                }
            }
            return true;
        }

        #endregion Hierarchical

        #region Linear

        /// <summary>
        /// Ordered list of change events.
        /// </summary>
        private List<AddressChange> mChangeList = new List<AddressChange>();

        /// <summary>
        /// Address change "event".
        ///
        /// Instances are immutable.
        /// </summary>
        public class AddressChange {
            // True if this is a region start, false if a region end.
            public bool IsStart { get; private set; }

            // Offset at which change occurs.  For end points, this at the offset AFTER
            // the last offset in a region.
            public int Offset { get; private set; }

            // Address at Offset after change.  For a region-end change, this is an address
            // in the parent's range.
            public int Address { get; private set; }

            // Reference to the AddressMapEntry that generated this entry.  The reference
            // will be the same for the "start" and "end" entries.
            public AddressMapEntry Entry { get; private set; }

            public AddressChange(bool isStart, int offset, int addr, AddressMapEntry ent) {
                IsStart = isStart;
                Offset = offset;
                Address = addr;
                Entry = ent;
            }
        }

        /// <summary>
        /// Generates a linear list of changes, using the data from the hierarchical representation.
        /// </summary>
        private void GenerateLinear() {
            // The top layer is treated specially, because we don't want to show the outer
            // no-address zone.  Instead, we synthesize fake zones in the gaps.
            List<AddressChange> changeList = new List<AddressChange>();
            int startOffset = 0;
            int extraNodes = 0;

            if (mTopNode.Children != null) {
                foreach (TreeNode node in mTopNode.Children) {
                    Debug.Assert(node.Entry.Length > 0);    // all floaters should be resolved

                    if (node.Entry.Offset != startOffset) {
                        // Insert a no-address zone here.
                        Debug.Assert(node.Entry.Offset > startOffset);
                        AddressMapEntry tmpEnt = new AddressMapEntry(startOffset,
                            node.Entry.Offset - startOffset, NON_ADDR, false, false);
                        changeList.Add(new AddressChange(true, startOffset, NON_ADDR, tmpEnt));
                        changeList.Add(new AddressChange(false, node.Entry.Offset, NON_ADDR, tmpEnt));
                        extraNodes++;
                    }

                    AddChangeEntry(changeList, node, NON_ADDR);

                    startOffset = node.Entry.Offset + node.Entry.Length;
                }
            }

            // Finish with a no-address zone if there's a gap.
            if (startOffset != mSpanLength) {
                Debug.Assert(startOffset < mSpanLength);
                AddressMapEntry tmpEnt = new AddressMapEntry(startOffset,
                    mSpanLength - startOffset, NON_ADDR, false, false);
                changeList.Add(new AddressChange(true, startOffset, NON_ADDR, tmpEnt));
                changeList.Add(new AddressChange(false, mSpanLength, NON_ADDR, tmpEnt));
                extraNodes++;
            }

            if (changeList.Count != (mRegionList.Count + extraNodes) * 2) {
                Debug.Assert(false, "Incorrect linear count: regions*2=" + (mRegionList.Count * 2) +
                    " extraNodes=" + extraNodes + " changeList=" + changeList.Count);
            }

            mChangeList = changeList;
        }

        public IEnumerator<AddressChange> AddressChangeIterator {
            get { return mChangeList.GetEnumerator(); }
        }

        /// <summary>
        /// Recursively adds tree nodes.
        /// </summary>
        /// <param name="changeList">List to which changes are added.</param>
        /// <param name="node">Node to add</param>
        /// <param name="parentStartAddr">Address at which node's start offset appears in
        ///   parent's region.</param>
        private void AddChangeEntry(List<AddressChange> changeList, TreeNode node,
                int parentStartAddr) {
            Debug.Assert(node.Entry.Length != FLOATING_LEN);
            int nextAddr = NON_ADDR;
            if (parentStartAddr != NON_ADDR) {
                nextAddr = parentStartAddr + node.Entry.Length;
            }
            AddressChange startChange = new AddressChange(true,
                node.Entry.Offset, node.Entry.Address, node.Entry);
            AddressChange endChange = new AddressChange(false,
                node.Entry.Offset + node.Entry.Length, nextAddr, node.Entry);

            changeList.Add(startChange);
            int curAddr = node.Entry.Address;
            if (node.Children != null) {
                foreach (TreeNode childNode in node.Children) {
                    int mySpaceAddr = NON_ADDR;
                    if (curAddr != NON_ADDR) {
                        // Adjust address in parent space by difference between start of
                        // parent and start of this node.
                        mySpaceAddr = curAddr + childNode.Entry.Offset - node.Entry.Offset;
                    }
                    AddChangeEntry(changeList, childNode, mySpaceAddr);
                }
            }
            changeList.Add(endChange);
        }

        private const string CRLF = "\r\n";

        /// <summary>
        /// Formats the address map for debugging.  (Does not use Asm65.Formatter, so is not
        /// suitable for display to the user.)
        /// </summary>
        public string FormatAddressMap() {
            StringBuilder sb = new StringBuilder();
            int depth = 0;
            AddressChange prevChange = null;

            sb.AppendLine("Address map, len=$" + mSpanLength.ToString("x4"));
            IEnumerator<AddressChange> iter = this.AddressChangeIterator;
            while (iter.MoveNext()) {
                AddressChange change = iter.Current;
                if (change.IsStart) {
                    if (prevChange != null && change.Offset != prevChange.Offset) {
                        // Start of region at new offset.  Output address info for space
                        // between previous start or end.
                        PrintAddressInfo(sb, depth, prevChange.Address,
                            change.Offset - prevChange.Offset);
                    }

                    // Start following end, or start following start after a gap.
                    PrintDepthLines(sb, depth);
                    sb.Append("+- +" + change.Offset.ToString("x6") + " START (");
                    PrintAddress(sb, change.Address);
                    sb.Append(")");
                    sb.Append(CRLF);

                    depth++;
                } else {
                    Debug.Assert(prevChange != null);
                    depth--;

                    if (change.Offset != prevChange.Offset) {
                        // End of region at new offset.  Output address info for space
                        // between previous start or end.
                        PrintAddressInfo(sb, depth + 1, prevChange.Address,
                            change.Offset - prevChange.Offset);
                    }

                    PrintDepthLines(sb, depth);
                    sb.Append("+- +" + change.Offset.ToString("x6") + " END (now ");
                    PrintAddress(sb, change.Address);
                    sb.Append(")");
                    sb.Append(CRLF);
                }

                prevChange = change;
            }
            Debug.Assert(depth == 0);

            return sb.ToString();
        }

        private static void PrintDepthLines(StringBuilder sb, int depth) {
            while (depth-- > 0) {
                sb.Append("| ");
            }
        }

        private static void PrintAddressInfo(StringBuilder sb, int depth,
                    int startAddr, int length) {
            PrintDepthLines(sb, depth);
            sb.Append(' ');
            if (startAddr == NON_ADDR) {
                sb.Append("-NA-");
            } else {
                PrintAddress(sb, startAddr);
                sb.Append(" - ");
                PrintAddress(sb, startAddr + length - 1);
            }
            sb.Append(" (length=$" + length.ToString("x4") + "/" + length + " bytes)");
            sb.Append(CRLF);
        }

        private static void PrintAddress(StringBuilder sb, int addr) {
            if (addr == NON_ADDR) {
                sb.Append("-NA-");
            } else {
                sb.Append("$");
                sb.Append(addr.ToString("x4"));
            }
        }

        #endregion Linear

        #region Unit tests

        private static void Test_Expect(AddResult expected, ref bool result, AddResult actual) {
            if (expected != actual) {
                Debug.WriteLine("test failed (expected=" + expected + ", actual=" + actual + ")");
                result = false;
            }
        }
        private static void Test_Expect(bool expected, ref bool result, bool actual) {
            if (expected != actual) {
                Debug.WriteLine("test failed (expected=" + expected + ", actual=" + actual + ")");
                result = false;
            }
        }

        private static void Test_Expect(int expected, ref bool result, int actual) {
            if (expected != actual) {
                Debug.WriteLine("test failed (expected=$" + expected.ToString("x4") + "/" +
                    expected + ", actual=$" + actual.ToString("x4") + "/" + actual + ")");
                result = false;
            }
        }

        private static bool Test_SimpleLinear() {
            const int mapLen = 0x8000;
            AddressMap map = new AddressMap(mapLen);
            bool result = true;

            const int off0 = 0x000000;
            const int len0 = 0x0200;
            const int adr0 = 0x1000;
            const int off1 = 0x000200;
            const int len1 = 0x0500;
            const int adr1 = 0x1200;
            const int off2 = 0x000700;
            const int len2 = 0x0300;
            const int adr2 = 0x1700;

            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(off0, len0, adr0, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(off1, len1, adr1, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(off2, len2, adr2, false));
            result &= map.DebugValidate();

            Test_Expect(AddResult.OverlapExisting, ref result,
                map.AddRegion(off0, len0, 0x1000, false));
            Test_Expect(AddResult.OverlapFloating, ref result,
                map.AddRegion(off0, FLOATING_LEN, 0x1000, false));
            Test_Expect(AddResult.StraddleExisting, ref result,
                map.AddRegion(off0 + 1, len0, 0x1000, false));
            Test_Expect(AddResult.InvalidValue, ref result,
                map.AddRegion(off0, mapLen + 1, 0x1000, false));

            // One region to wrap them all.  Add then remove.
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(off0, mapLen, 0x0000, false));
            Test_Expect(true, ref result, map.RemoveRegion(off0, mapLen));
            Test_Expect(false, ref result, map.RemoveRegion(off0, mapLen));

            Test_Expect(adr0, ref result, map.OffsetToAddress(off0));
            Test_Expect(adr1, ref result, map.OffsetToAddress(off1));
            Test_Expect(adr2, ref result, map.OffsetToAddress(off2));
            Test_Expect(adr0 + 0x100, ref result, map.OffsetToAddress(off0 + 0x100));
            Test_Expect(NON_ADDR, ref result, map.OffsetToAddress(0x004000));   // hole in map
            Test_Expect(NON_ADDR, ref result, map.OffsetToAddress(mapLen));     // bad offset

            Test_Expect(0x000000, ref result, map.AddressToOffset(0x000000, 0x1000));
            Test_Expect(0x000000, ref result, map.AddressToOffset(0x000200, 0x1000));
            Test_Expect(0x000000, ref result, map.AddressToOffset(0x000700, 0x1000));
            Test_Expect(0x000250, ref result, map.AddressToOffset(0x000000, 0x1250));
            Test_Expect(0x000250, ref result, map.AddressToOffset(0x000200, 0x1250));
            Test_Expect(0x000250, ref result, map.AddressToOffset(0x000700, 0x1250));
            Test_Expect(0x0009ff, ref result, map.AddressToOffset(0x0001ff, 0x19ff));
            Test_Expect(0x0009ff, ref result, map.AddressToOffset(0x0006ff, 0x19ff));
            Test_Expect(0x0009ff, ref result, map.AddressToOffset(0x0009ff, 0x19ff));
            Test_Expect(-1, ref result, map.AddressToOffset(0x000000, 0x7000));

            result &= map.DebugValidate();
            return result;
        }

        private static bool Test_SimpleFloatGap() {
            const int mapLen = 0x8000;
            AddressMap map = new AddressMap(mapLen);
            bool result = true;

            const int off0 = 0x001000;
            const int len0 = FLOATING_LEN;
            const int adr0 = 0x1000;
            const int off1 = 0x004000;
            const int len1 = 0x3000;
            const int adr1 = 0x1200;
            const int off2 = 0x005000;
            const int len2 = 0x0100;
            const int adr2 = NON_ADDR;

            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(off0, len0, adr0, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(off1, len1, adr1, false));

            // Try to remove the implicit no-address zone.
            Test_Expect(false, ref result, map.RemoveRegion(0, off0));

            // Add non-addressable area into the middle of the second region.
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(off2, len2, adr2, false));

            Test_Expect(adr0, ref result, map.OffsetToAddress(off0));
            Test_Expect(adr1, ref result, map.OffsetToAddress(off1));
            Test_Expect(adr2, ref result, map.OffsetToAddress(off2));
            Test_Expect(adr0 + 1, ref result, map.OffsetToAddress(off0 + 1));
            Test_Expect(adr1 + len2, ref result, map.OffsetToAddress(off1 + len2));
            Test_Expect(NON_ADDR, ref result, map.OffsetToAddress(off1 + len1));

            Test_Expect(-1, ref result, map.AddressToOffset(0x000000, 0x0000));
            Test_Expect(0x001005, ref result, map.AddressToOffset(0x000000, 0x1005));
            // Find the "correct" $21ff.
            Test_Expect(0x0021ff, ref result, map.AddressToOffset(0x000000, 0x21ff));
            Test_Expect(0x004fff, ref result, map.AddressToOffset(0x004000, 0x21ff));
            // There's only one $2205.
            Test_Expect(0x002205, ref result, map.AddressToOffset(0x000000, 0x2205));
            Test_Expect(0x002205, ref result, map.AddressToOffset(0x004000, 0x2205));

            result &= map.DebugValidate();
            return result;
        }

        private static bool Test_Nested() {
            AddressMap map = new AddressMap(0x8000);
            bool result = true;
            // Nested with shared start offset.
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x000100, 0x0400, 0x4000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x000100, 0x0100, 0x7000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x000100, 0x0300, 0x5000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x000100, 0x0200, 0x6000, false));
            // Add a couple of floaters.
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x0000ff, FLOATING_LEN, 0x30ff, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x000101, FLOATING_LEN, 0x3101, false));
            Test_Expect(AddResult.OverlapFloating, ref result,
                map.AddRegion(0x000100, FLOATING_LEN, 0x3100, false));

            // Nested with shared end offset.
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x000fff, FLOATING_LEN, 0x3fff, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x001200, 0x0200, 0x6000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x001000, 0x0400, 0x4000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x001100, 0x0300, 0x5000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x001300, 0x0100, 0x7000, false));
            // Single-byte region at start and end.
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x001200, 1, 0x8200, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x0013ff, 1, 0x83ff, false));

            // Nested with no common edge, building from outside-in.
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x002000, 0x0800, 0x4000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x002100, 0x0600, 0x5000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x002200, 0x0400, 0x6000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x002300, 0x0200, 0x7000, false));

            // Nested with no common edge, building from inside-out.
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x003300, 0x0200, 0x7000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x003200, 0x0400, 0x6000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x003100, 0x0600, 0x5000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x003000, 0x0800, 0x4000, false));

            // Try floater then overlap.
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x004000, FLOATING_LEN, 0x8000, false));
            Test_Expect(AddResult.OverlapFloating, ref result,
                map.AddRegion(0x004000, 0x100, 0x8000, false));
            Test_Expect(true, ref result, map.RemoveRegion(0x004000, FLOATING_LEN));

            Test_Expect(0x30ff, ref result, map.OffsetToAddress(0x0000ff));
            Test_Expect(0x7000, ref result, map.OffsetToAddress(0x000100));
            Test_Expect(0x3101, ref result, map.OffsetToAddress(0x000101));
            Test_Expect(0x5000, ref result, map.OffsetToAddress(0x001100));
            Test_Expect(0x7000, ref result, map.OffsetToAddress(0x001300));

            // The first chunk has $5000, but it's a shared start with children.  So we'll
            // find it in the second chunk.
            Test_Expect(0x001100, ref result, map.AddressToOffset(0x000000, 0x5000));
            // It's also in the 3rd/4th chunks, so we'll find it there if we start there.
            Test_Expect(0x002100, ref result, map.AddressToOffset(0x002300, 0x5000));
            Test_Expect(0x003100, ref result, map.AddressToOffset(0x003000, 0x5000));

            result &= map.DebugValidate();
            return result;
        }

        private static bool Test_Cross() {
            const int mapLen = 0x4000;
            AddressMap map = new AddressMap(mapLen);
            bool result = true;

            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x000000, 0x2000, 0x8000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x002000, 0x2000, 0x8000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x002100, 0x0200, 0xe100, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x003100, 0x0200, 0xf100, false));

            Test_Expect(0x003105, ref result, map.AddressToOffset(0x000000, 0xf105));
            Test_Expect(0x003105, ref result, map.AddressToOffset(0x002100, 0xf105));
            Test_Expect(0x003105, ref result, map.AddressToOffset(0x003100, 0xf105));

            Test_Expect(0x002105, ref result, map.AddressToOffset(0x000000, 0xe105));
            Test_Expect(0x002105, ref result, map.AddressToOffset(0x002100, 0xe105));
            Test_Expect(0x002105, ref result, map.AddressToOffset(0x003100, 0xe105));

            // $8105 doesn't exist in the second chunk because there's a hole there.  We
            // find it in the first chunk instead.
            Test_Expect(0x000105, ref result, map.AddressToOffset(0x000000, 0x8105));
            Test_Expect(0x000105, ref result, map.AddressToOffset(0x002000, 0x8105));

            // $8400 exists in the first chunk, and in a child of the second chunk.  If
            // we start anywhere in the second chunk we'll find the second address.
            Test_Expect(0x000400, ref result, map.AddressToOffset(0x000000, 0x8400));
            Test_Expect(0x002400, ref result, map.AddressToOffset(0x002000, 0x8400));
            Test_Expect(0x002400, ref result, map.AddressToOffset(0x002100, 0x8400));
            Test_Expect(0x002400, ref result, map.AddressToOffset(0x003100, 0x8400));

            Test_Expect(0x001100, ref result, map.AddressToOffset(0x000000, 0x9100));

            result &= map.DebugValidate();
            return result;
        }

        private static bool Test_Pyramids() {
            const int mapLen = 0xc000;
            AddressMap map = new AddressMap(mapLen);
            bool result = true;

            // Pyramid shape, all regions start at same address except last.
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x000000, 0x6000, 0x8000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x001000, 0x4000, 0x8000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x002000, 0x2000, 0x7fff, false));

            // Second pyramid.
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x006000, 0x6000, 0x8000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x007000, 0x4000, 0x8000, false));
            Test_Expect(AddResult.Okay, ref result,
                map.AddRegion(0x008000, 0x2000, 0x8000, false));

            string mapStr = map.FormatAddressMap();     // DEBUG - format the map and
            Debug.WriteLine(mapStr);                    // DEBUG - print it to the console

            // Children take priority over the start node.
            Test_Expect(0x002001, ref result, map.AddressToOffset(0x000000, 0x8000));
            Test_Expect(0x003000, ref result, map.AddressToOffset(0x000000, 0x8fff));
            Test_Expect(0x002001, ref result, map.AddressToOffset(0x001000, 0x8000));
            Test_Expect(0x003000, ref result, map.AddressToOffset(0x001000, 0x8fff));
            Test_Expect(0x002001, ref result, map.AddressToOffset(0x002000, 0x8000));
            Test_Expect(0x002000, ref result, map.AddressToOffset(0x000000, 0x7fff));

            Test_Expect(0x005000, ref result, map.AddressToOffset(0x000000, 0xd000));
            Test_Expect(0x005000, ref result, map.AddressToOffset(0x003000, 0xd000));

            Test_Expect(-1, ref result, map.AddressToOffset(0x000000, 0xc000));
            Test_Expect(-1, ref result, map.AddressToOffset(0x000000, 0xcfff));

            Test_Expect(0x008000, ref result, map.AddressToOffset(0x006000, 0x8000));
            Test_Expect(0x008000, ref result, map.AddressToOffset(0x007000, 0x8000));
            Test_Expect(0x008000, ref result, map.AddressToOffset(0x008000, 0x8000));
            Test_Expect(0x008000, ref result, map.AddressToOffset(0x00bfff, 0x8000));

            // $7fff doesn't exist in second chunk, so we have to go back to first to find it.
            Test_Expect(0x002000, ref result, map.AddressToOffset(0x008000, 0x7fff));
            Test_Expect(-1, ref result, map.AddressToOffset(0x008000, 0xa000));

            // inside
            Test_Expect(true, ref result, map.IsRangeUnbroken(0x000000, 1));
            Test_Expect(true, ref result, map.IsRangeUnbroken(0x007000, 0x0800));
            // at edges
            Test_Expect(true, ref result, map.IsRangeUnbroken(0x000ffe, 2));
            Test_Expect(true, ref result, map.IsRangeUnbroken(0x001000, 2));
            Test_Expect(true, ref result, map.IsRangeUnbroken(0x007000, 0x1000));
            // crossing edge
            Test_Expect(false, ref result, map.IsRangeUnbroken(0x000fff, 2));
            // fully encapsulating
            Test_Expect(false, ref result, map.IsRangeUnbroken(0x005500, 0x1000));

            result &= map.DebugValidate();
            return result;
        }

        public static bool Test() {
            bool ok = true;
            ok &= Test_SimpleLinear();
            ok &= Test_SimpleFloatGap();
            ok &= Test_Nested();
            ok &= Test_Cross();
            ok &= Test_Pyramids();

            Debug.WriteLine("AddressMap: test complete (ok=" + ok + ")");
            return ok;
        }

        #endregion Unit tests
    }
}
