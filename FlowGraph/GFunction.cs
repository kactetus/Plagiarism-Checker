﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

#pragma warning disable CS1591

namespace FlowGraph
{
	/// <summary>
	/// Represents a function in the GIMPLE file.
	/// </summary>
	[DebuggerDisplay ( "{Name} in {FileName} : Threshold = {Threshold}" )]
	public class GFunction
	{
		public static class Defaults
		{
			public static readonly decimal Threshold = .95m;
			public static readonly int Iterations = 1;
			public static bool DumpIntermediateGimple = true;
		}

		/// <summary>
		/// All lines in the GIMPLE file.
		/// </summary>
		public List<string> Gimple { get; private set; }

		/// <summary>
		/// Name of the file the function belongs to.
		/// </summary>
		public string FileName { get; private set; }

		/// <summary>
		/// Name of the function.
		/// </summary>
		public string Name { get; private set; }

		/// <summary>
		/// Threshold to be used when comparing variables (0 - 1)
		/// Default: 0.95m
		/// </summary>
		public decimal Threshold { get; set; } = Defaults.Threshold;

		/// <summary>
		/// funcdef_no of the function.
		/// </summary>
		public int Funcdef_no { get; private set; }

		/// <summary>
		/// decl_uid of the function.
		/// </summary>
		public int Decl_uid { get; private set; }

		/// <summary>
		/// symbol_order of the function.
		/// </summary>
		public int Symbol_order { get; private set; }

		/// <summary>
		/// Number of iterations performed when comparing 2 <see cref="GFunction"/>s.
		/// </summary>
		public int Iterations { get; set; } = Defaults.Iterations;

		/// <summary>
		/// Functions formal arguments.
		/// </summary>
		public string Args { get; private set; }

		/// <summary>
		/// All the <see cref="GBaseBlock"/>s in the function.
		/// </summary>
		public List<GBaseBlock> Blocks { get; private set; } = new List<GBaseBlock> ( );

		/// <summary>
		/// <see cref="Dictionary{TKey, TValue}"/> of <see cref="int"/> mapped to <see cref="GBaseBlock"/> by the number.
		/// </summary>
		public Dictionary<int, GBaseBlock> BlockMap { get; private set; } = new Dictionary<int, GBaseBlock> ( );

		/// <summary>
		/// <see cref="HashSet{T}"/> of all the <see cref="GVar"/> declared in the function.
		/// </summary>
		public HashSet<GVar> GVarsDecl { get; set; } = new HashSet<GVar> ( );

		/// <summary>
		/// <see cref="HashSet{T}"/> of all the variable names used in the function as <see cref="string"/>.
		/// </summary>
		public HashSet<string> GVarsUsed { get; set; } = new HashSet<string> ( );

		/// <summary>
		/// <see cref="Dictionary{TKey, TValue}"/> of <see cref="string"/> mapped to <see cref="GVar"/> by the variable name with its GIMPLE suffix.
		/// </summary>
		public Dictionary<string, GVar> UsedToDeclMap { get; set; } = new Dictionary<string, GVar> ( );

		/// <summary>
		/// <see cref="List{T}"/> of <see cref="GimpleStmt"/>s used in the whole function.
		/// </summary>
		public List<GimpleStmt> GStatements => Blocks.SelectMany ( b => b.GStatements ).ToList ( );

		/// <summary>
		/// Whether or not the <see cref="GFunction"/> should be dumped after each round of processing.
		/// </summary>
		public bool DumpIntermediateGimple { get; set; } = Defaults.DumpIntermediateGimple;

		public string Folder => Path.GetFileNameWithoutExtension ( FileName );

		/// <summary>
		/// Build all the <see cref="GBaseBlock"/>s and other members using the GIMPLE input file.
		/// </summary>
		/// <param name="gimple"><see cref="List{T}"/> of <see cref="string"/> from the GIMPLE source.</param>
		/// <param name="fileName">Name of the file this function belongs to.</param>
		public GFunction ( List<string> gimple, string fileName = "" )
		{
			if ( Iterations < 1 )
				Iterations = 1;
			Initialize ( gimple );
			this.FileName = Path.GetFileName ( fileName );
			DumpGimple ( $"{Folder}\\Clean.GIMPLE" );
		}

		private void Initialize ( List<string> gimple, bool doNormalizeConditionals = false, bool doReorderPhi = false )
		{
			this.Gimple = gimple;
			string pattern = @";; Function (?<name>\S*) \((\S*, funcdef_no=(?<funcdef_no>[0-9]*), decl_uid=(?<decl_uid>[0-9]*), cgraph_uid=[0-9]*, symbol_order=(?<symbol_order>[0-9]*))\).*";
			bool insideFunc = false, insideBody = false;
			List<string> body = new List<string> ( );
			foreach ( var line in gimple )
			{
				if ( Regex.IsMatch ( line, pattern ) )
				{
					insideFunc = true;
					var match = Regex.Match ( line, pattern );
					Name = match.Groups["name"].Value;
					Funcdef_no = Convert.ToInt32 ( match.Groups["funcdef_no"].Value );
					Decl_uid = Convert.ToInt32 ( match.Groups["decl_uid"].Value );
					Symbol_order = Convert.ToInt32 ( match.Groups["symbol_order"].Value );
				}
				if ( !insideFunc )
					continue;
				if ( !insideBody && line != "{" )
					continue;
				insideBody = true;
				if ( new string[] { "{", "}" }.Contains ( line ) )
					continue;
				body.Add ( line );
			}

			Blocks = GBaseBlock.GetBaseBlocks ( body, doReorderPhi );

			if ( doNormalizeConditionals )
				NormalizeConditionals ( );

			GetVarsDecl ( body );

			GetVarsUsed ( );

			BuildUsedToDeclMap ( );

			RemoveCasts ( );

			GetVarsUsed ( );

			BuildUsedToDeclMap ( );

			BuildDUC ( );

			CreateEdges ( );
		}

		private void GetVarsDecl ( List<string> gimple )
		{
			GVarsDecl.Clear ( );
			foreach ( var line in gimple )
			{
				if ( line == "" || GBBStmt.Matches ( line ) )
					break;
				string declPattern = @"\s*(?<type>.*) (?<name>\S*);";
				var match = Regex.Match ( line, declPattern );
				var gVar = new GVar
				{
					Name = match.Groups["name"].Value,
					Type = match.Groups["type"].Value
				};
				GVarsDecl.Add ( gVar );
			}
		}

		private void NormalizeConditionals ( )
		{
			foreach ( var bb in Blocks )
			{
				var conditionals = bb.GStatements.OfType<GCondStmt> ( ).ToList ( );
				if ( conditionals.Count == 0 )
					continue;
				foreach ( var c in conditionals )
				{
					if ( c.Normalize ( ) )
					{
						if ( bb.GStatements.Count >= c.Linenum + 3 )
						{
							int x = ( bb.GStatements[c.Linenum + 1] as GGotoStmt ).Number;
							int y = ( bb.GStatements[c.Linenum + 3] as GGotoStmt ).Number;
							SwapBaseBlocks ( x, y );
							NormalizeConditionals ( );
						}
						return;
						if (
							bb.GStatements.Count >= c.Linenum + 3 &&
							bb.GStatements[c.Linenum + 1] is GGotoStmt &&
							bb.GStatements[c.Linenum + 3] is GGotoStmt
							)
						{
							int x = ( bb.GStatements[c.Linenum + 1] as GGotoStmt ).Number;
							int y = ( bb.GStatements[c.Linenum + 3] as GGotoStmt ).Number;
							SwapBaseBlocks ( x, y );
							NormalizeConditionals ( );
							return;
						}
					}
				}
			}
		}

		private void SwapBaseBlocks ( int x, int y )
		{
			var bbX = Blocks.FirstOrDefault ( b => b.Number == x );
			var bbY = Blocks.FirstOrDefault ( b => b.Number == y );
			if ( bbX == null || bbY == null )
				return;

			var xStatements = bbX.GStatements.ToList ( );
			var yStatements = bbY.GStatements.ToList ( );

			{
				var temp = xStatements[0];
				xStatements[0] = yStatements[0];
				yStatements[0] = temp;
				//if (
				//	xStatements.Count > 2 &&
				//	xStatements.Last ( ) is GGotoStmt &&
				//	!( xStatements[xStatements.Count - 2] is GElseStmt )
				//	)
				//{
				//	yStatements.Add ( xStatements.Last ( ) );
				//	xStatements.Remove ( xStatements.Last ( ) );
				//}
				//else if (
				//	yStatements.Count > 2 &&
				//	yStatements.Last ( ) is GGotoStmt &&
				//	!( yStatements[yStatements.Count - 2] is GElseStmt )
				//	)
				//{
				//	xStatements.Add ( yStatements.Last ( ) );
				//	yStatements.Remove ( yStatements.Last ( ) );
				//}
			}

			bbX.GStatements = yStatements;
			bbY.GStatements = xStatements;
		}

		private void GetVarsUsed ( )
		{
			GVarsUsed.Clear ( );
			foreach ( var block in Blocks )
				GVarsUsed.UnionWith ( block.Vars );
		}

		private void BuildUsedToDeclMap ( )
		{
			UsedToDeclMap.Clear ( );
			foreach ( var used in GVarsUsed )
			{
				foreach ( var v in GVarsDecl )
				{
					if ( Regex.IsMatch ( used, $"^{v.Name}(_[0123456789]+)?$" ) )
					{
						UsedToDeclMap[used] = v;
					}
				}
				if ( !UsedToDeclMap.ContainsKey ( used ) )
				{
					var v = new GVar ( )
					{
						Name = used,
						Type = "void *"
					};
					GVarsDecl.Add ( v );
					UsedToDeclMap[used] = v;
				}
			}

			foreach ( var v in GVarsDecl )
				UsedToDeclMap[v.Name] = v;
		}

		private void BuildDUC ( )
		{
			foreach ( var v in GVarsDecl )
			{
				v.DucAssignments.Data.Clear ( );
				v.DucReferences.Data.Clear ( );
			}
			foreach ( var block in Blocks )
			{
				GVarsUsed.UnionWith ( block.Vars );
				foreach ( var stmt in block.GStatements )
				{
					if ( stmt is GAssignStmt || stmt is GPhiStmt || stmt is GOptimizedStmt )
					{
						UsedToDeclMap[stmt.Vars[0]].DucAssignments.Add ( block.Number, stmt.Linenum );
						for ( int i = 1; i < stmt.Vars.Count; ++i )
							UsedToDeclMap[stmt.Vars[i]].DucReferences.Add ( block.Number, stmt.Linenum );
					}
					else
					{
						foreach ( var v in stmt.Vars )
							UsedToDeclMap[v].DucReferences.Add ( block.Number, stmt.Linenum );
					}
				}
			}
		}

		public void Rename ( string oldName, string newName )
		{
			Blocks.ForEach ( b => b.Rename ( oldName, newName ) );

			if ( GVarsDecl.Count ( v => v.Name == oldName ) != 0 )
				GVarsDecl.Where ( v => v.Name == oldName ).FirstOrDefault ( ).Name =
					UsedToDeclMap.ContainsKey ( newName ) ? UsedToDeclMap[newName].Name : newName;
			GVarsDecl = new HashSet<GVar> ( GVarsDecl );

			if ( GVarsUsed.Remove ( oldName ) )
			{
				GVarsUsed.Add ( newName );
				if ( UsedToDeclMap.ContainsKey ( oldName ) )
				{
					UsedToDeclMap[newName] = UsedToDeclMap[oldName];
					UsedToDeclMap.Remove ( oldName );
				}
			}
		}

		private void CreateEdges ( )
		{
			foreach ( var block in Blocks )
				BlockMap[block.Number] = block;

			foreach ( var block in Blocks )
			{
				var numbers = block.GetOutEdges ( );
				foreach ( var number in numbers )
				{
					if ( !BlockMap.ContainsKey ( number ) )
						continue;
					block.OutEdges.Add ( BlockMap[number] );
					BlockMap[number].InEdges.Add ( block );
				}
			}
		}

		private void RemoveCasts ( )
		{
			GStatements
				.OfType<GCastStmt> ( )
				.ToList ( )
				.ForEach ( cast => Rename ( cast.Assignee, cast.V ) );
			Blocks.ForEach ( b =>
			{
				b.GStatements.RemoveAll ( s => s is GCastStmt );
				b.RenumberLines ( );
			}
			);
		}

		private void PreProcess ( GFunction gFunc )
		{
			for ( int times = 0; times != Iterations; ++times )
			{
				var varMap = GetVarMap ( gFunc )
					.ToDictionary ( x => x.Key.Name, x => x.Value.Select ( y => y.Name ).ToList ( ) );
				var revVarMap = varMap
					.GroupBy ( x => string.Join ( ", ", x.Value.OrderBy ( v => v ) ) )
					.Where ( x => x.Count ( ) > 1 )
					.ToDictionary ( x => x.Key, x => x.Select ( y => y.Key ).ToList ( ) );

				var lhsVars = this.GStatements.SelectMany ( b => b.Vars ).Distinct ( ).ToList ( );
				var rhsVars = gFunc.GStatements.SelectMany ( b => b.Vars ).Distinct ( ).ToList ( );

				foreach ( var rhsVar in lhsVars )
				{
					if ( !UsedToDeclMap.ContainsKey ( rhsVar ) )
						continue;
					this.Rename ( rhsVar, UsedToDeclMap[rhsVar].Name );
				}
				foreach ( var rhsVar in rhsVars )
				{
					if ( !gFunc.UsedToDeclMap.ContainsKey ( rhsVar ) )
						continue;
					gFunc.Rename ( rhsVar, gFunc.UsedToDeclMap[rhsVar].Name );
				}

				foreach ( var pair in varMap )
				{
					if ( revVarMap.ContainsKey ( pair.Value.First ( ) ) )
						this.Rename ( pair.Key, pair.Value.First ( ) );
					else
						foreach ( var v in pair.Value )
							gFunc.Rename ( v, pair.Key );
				}

				gFunc.GetVarsUsed ( );
				gFunc.BuildUsedToDeclMap ( );
				gFunc.BuildDUC ( );
				this.GetVarsUsed ( );
				this.BuildUsedToDeclMap ( );
				this.BuildDUC ( );

				if ( DumpIntermediateGimple )
				{
					var lhsFileName = Path.GetFileNameWithoutExtension ( FileName );
					var rhsFileName = Path.GetFileNameWithoutExtension ( gFunc.FileName );
					DumpGimple ( $"{Folder}\\{lhsFileName}_{rhsFileName}_pass_{times + 1}.GIMPLE" );
					gFunc.DumpGimple ( $"{gFunc.Folder}\\{rhsFileName}_{lhsFileName}_pass_{times + 1}.GIMPLE" );
				}
			}
		}

		/// <summary>
		/// This is messed up and probably stupid of me to implement...
		/// </summary>
		/// <param name="gFunc"></param>
		[Obsolete ( "PreProcessLineByLine is obsolete, use PreProcess instead", true )]
		private void PreProcessLineByLine ( GFunction gFunc )
		{
			var varMap = GetVarMap ( gFunc );

			foreach ( var pair in varMap )
			{
				var duc = pair.Value
					.SelectMany ( x => x.DucAssignments.Data )
					.Union ( pair.Value.SelectMany ( x => x.DucReferences.Data ) )
					.ToList ( );

				foreach ( var entry in duc )
				{
					if ( BlockMap[entry.block].GStatements.Count < entry.line )
						continue;
					if ( gFunc.BlockMap[entry.block].GStatements.Count < entry.line )
						continue;

					var lhsStmt = BlockMap[entry.block].GStatements[entry.line - 1];
					var rhsStmt = gFunc.BlockMap[entry.block].GStatements[entry.line - 1];

					if ( rhsStmt.Vars.Count == lhsStmt.Vars.Count )
					{
						for ( int i = 0; i < lhsStmt.Vars.Count; ++i )
						{
							string lvar = lhsStmt.Vars[i];
							string rvar = rhsStmt.Vars[i];
							if ( gFunc.UsedToDeclMap.ContainsKey ( rvar ) &&
								varMap.ContainsKey ( UsedToDeclMap[lvar] ) &&
								varMap[UsedToDeclMap[lvar]].Contains ( gFunc.UsedToDeclMap[rvar] ) )
								rhsStmt.Rename ( rvar, lvar );
						}
					}
				}
			}
		}

		private static int LevenshteinDistance ( List<GimpleStmt> a, List<GimpleStmt> b )
		{
			if ( a.Count == 0 || b.Count == 0 )
				return 0;

			int lengthA = a.Count;
			int lengthB = b.Count;

			var distances = new int[lengthA + 1, lengthB + 1];
			for ( int i = 0; i <= lengthA; distances[i, 0] = i++ )
				;
			for ( int j = 0; j <= lengthB; distances[0, j] = j++ )
				;

			for ( int i = 1; i <= lengthA; i++ )
			{
				for ( int j = 1; j <= lengthB; j++ )
				{
					int cost = b[j - 1] == a[i - 1] ? 0 : 1;
					distances[i, j] = Math.Min
						(
						Math.Min ( distances[i - 1, j] + 1, distances[i, j - 1] + 1 ),
						distances[i - 1, j - 1] + cost
						);
				}
			}

			return distances[lengthA, lengthB];
		}

		public decimal Compare ( GFunction gFunc, bool doNormalizeConditionals = false, bool doReorderPhi = false )
		{
			PreProcess ( gFunc );
			decimal count = 0;
			var lhsStmts = GStatements;
			var rhsStmts = gFunc.GStatements;
			lhsStmts.RemoveAll ( s => s is GBBStmt );
			rhsStmts.RemoveAll ( s => s is GBBStmt );
			count = LevenshteinDistance ( lhsStmts, rhsStmts );
			decimal result = 
				100m -
				100m * count /
				Math.Max ( lhsStmts.Count, rhsStmts.Count );

			gFunc.Reset ( doNormalizeConditionals, doReorderPhi );

			return result;
		}

		public static decimal Compare ( GFunction lhs, GFunction rhs )
		{
			var results = new List<decimal> ( );
			var flags = new List<(bool doNormalizeConditionals, bool doReorderPhi)>
			{
				(false, false),
				(false, true),
				(true, false),
				(true, true)
			};
			foreach ( var lhsFlag in flags )
			{
				lhs.Reset ( lhsFlag.doNormalizeConditionals, lhsFlag.doReorderPhi );
				foreach ( var rhsFlag in flags )
				{
					rhs.Reset ( rhsFlag.doNormalizeConditionals, rhsFlag.doReorderPhi );
					results.Add ( lhs.Compare ( rhs, rhsFlag.doNormalizeConditionals, rhsFlag.doReorderPhi ) );
				}
			}

			return results.Max ( );
		}

		public void DumpGimple ( string path )
		{
			using ( var stream = new StreamWriter ( path ) )
			{
				foreach ( var block in Blocks )
				{
					block.GStatements.ForEach ( s => stream.WriteLine ( s ) );
					stream.WriteLine ( );
				}
			}
		}

		public void Reset ( bool doNormalizeConditionals = false, bool doReorderPhi = false ) =>
			Initialize ( Gimple, doNormalizeConditionals, doReorderPhi );

		/// <summary>
		/// Compare the <see cref="GFunction.Blocks"/> of both <see cref="GFunction"/> using <code>SequenceEqual()</code>.
		/// </summary>
		/// <param name="lhs">LHS</param>
		/// <param name="rhs">RHS</param>
		/// <returns></returns>
		public static bool operator == ( GFunction lhs, GFunction rhs )
		{
			if ( object.ReferenceEquals ( rhs, null ) )
				return object.ReferenceEquals ( lhs, null );
			else if ( object.ReferenceEquals ( lhs, null ) )
				return false;

			lhs.PreProcess ( rhs );
			bool result = lhs.Blocks.SequenceEqual ( rhs.Blocks );

			rhs.Reset ( );

			return result;
		}

		/// <summary>
		/// Negation of <see cref="operator !="/>.
		/// </summary>
		/// <param name="lhs">LHS</param>
		/// <param name="rhs">RHS</param>
		/// <returns></returns>
		public static bool operator != ( GFunction lhs, GFunction rhs )
		{
			return !( lhs == rhs );
		}

		public override bool Equals ( object obj )
		{
			if ( obj is GFunction )
				return this == ( obj as GFunction );
			return base.Equals ( obj );
		}

		public override int GetHashCode ( ) => base.GetHashCode ( );

		public Dictionary<GVar, List<GVar>> GetVarMap ( GFunction gFunc )
		{
			Dictionary<GVar, List<GVar>> varMap = new Dictionary<GVar, List<GVar>> ( );

			Dictionary<GVar, Dictionary<GVar, decimal>> correlation = new Dictionary<GVar, Dictionary<GVar, decimal>> ( );
			foreach ( var v1 in GVarsDecl )
			{
				foreach ( var v2 in gFunc.GVarsDecl )
				{
					if ( !correlation.ContainsKey ( v1 ) )
						correlation[v1] = new Dictionary<GVar, decimal> ( );
					correlation[v1][v2] = v1.Map ( v2 );
				}
				var probable = new List<GVar> ( );
				if ( correlation.Count != 0 )
					probable = correlation[v1]
					.Where ( x => x.Value != 0m )
					.OrderBy ( x => x.Value * -1m )
					.Select ( x => x.Key )
					.ToList ( );
				var minimal = new List<GVar> ( );
				foreach ( var v in probable )
				{
					if ( !v.IsSubsetOf ( v1 ) )
						continue;
					minimal.Add ( v );
					if ( v1.Map ( minimal ) > Threshold )
					{
						varMap[v1] = minimal;
						break;
					}
				}
			}

			foreach ( var v1 in gFunc.GVarsDecl )
			{
				foreach ( var v2 in GVarsDecl )
				{
					var probable = correlation
						.Where ( x => x.Value[v1] != 0m )
						.OrderBy ( x => x.Value[v1] * -1m )
						.Select ( x => x.Key )
						.ToList ( );
					var minimal = new List<GVar> ( );
					foreach ( var v in probable )
					{
						minimal.Add ( v );
						if ( v1.Map ( minimal ) > Threshold )
						{
							minimal.ForEach ( m =>
							{
								if ( !varMap.ContainsKey ( m ) )
									varMap[m] = new List<GVar> { v1 };
							}
							);
							break;
						}
					}
				}
			}

			return varMap;
		}
	}
}