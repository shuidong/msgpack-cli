﻿#region -- License Terms --
//
// MessagePack for CLI
//
// Copyright (C) 2010 FUJIWARA, Yusuke
//
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//
//        http://www.apache.org/licenses/LICENSE-2.0
//
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.
//
#endregion -- License Terms --
#define INLINED

using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.Diagnostics;
namespace MsgPack
{
	/// <summary>
	///		Implements streaming unpacking. This object is stateful.
	/// </summary>
	internal sealed class StreamingUnpacker
	{
#if PERF_TRACE
		private enum TracePoint : byte
		{
			TransitStage,
			AddToContextCollection,
			ParseHeader,
			TransitToUnpackRawBytes,
			UnpackHeaderAndFixedValue,
			Finish,
			Begin
		}
#endif
		private struct TraceEntry
		{
#if PERF_TRACE
			public readonly long Timestamp;
			public readonly TracePoint TracePoint;
			public TraceEntry( TracePoint tp )
			{
				this.TracePoint = tp;
				this.Timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
			}
#endif
		}

		/// <summary>
		///		Stacked states for context collection.
		/// </summary>
		private readonly CollectionUnpackagingState _collectionState = new CollectionUnpackagingState();

		/// <summary>
		///		Stage of this state machine instance.
		/// </summary>
		private Stage _stage;

		/// <summary>
		///		Context header of unpackaging message.
		/// </summary>
		private MessagePackHeader _contextValueHeader;

		/// <summary>
		///		Buffer for unpackaging scalar or binary value.
		/// </summary>
		//private BytesBuffer _scalarBuffer;

		private readonly byte[] _primitiveBuffer = new byte[ sizeof( ulong ) ];
		private uint _primitiveSize;
		private byte[] _rawBuffer;
		private uint _bufferOffset;

		/// <summary>
		///		Initialize new instance.
		/// </summary>
		public StreamingUnpacker() { }

		/// <summary>
		///		Try unpack object from specified source.
		/// </summary>
		/// <param name="source">Input source to unpack.</param>
		/// <returns>
		/// 
		/// </returns>
		/// <remarks>
		///		<para>
		///			When this method returns null, caller can feed extra bytes to <paramref name="source"/> and invoke this again. 
		///			It could succeed because this instance preserves previous invocation state, and required bytes are supplied.
		///		</para>
		///		<para>
		///			When this method completes unpackaging single <see cref="MessagePackObject"/> tree,
		///			this method stops iterating <paramref name="source"/> (via <see cref="IEnumerator&lt;T&gt;"/>.
		///			This behavior is notified via <see cref="IDisposable.Dispose">IEnumerator&lt;T&gt;.Dispose()</see> method.
		///		</para>
		/// </remarks>
		public MessagePackObject? Unpack( IEnumerable<byte> source )
		{
			// FIXME:BULK LOAD
			Contract.Assert( source != null );

			List<TraceEntry> trace =
#if PERF_TRACE
				new List<TraceEntry>( 12000 );
#else
 null;
#endif
#if PERF_TRACE
			trace.Add( new TraceEntry( TracePoint.Begin ) );
			var t = System.Diagnostics.Stopwatch.GetTimestamp();
			try
			{
#endif
			var segmentatedSource = source as ISegmentLengthRecognizeable ?? NullSegmentLengthRecognizeable.Instance;
			foreach ( var b in source )
			{
#if INLINED
				MessagePackObject result = default( MessagePackObject );

				#region Transit

				switch ( this._stage )
				{
					case Stage.UnpackCollectionLength:
					{
						//this._scalarBuffer = this._scalarBuffer.Feed( b );
						this._primitiveBuffer[ this._bufferOffset ] = b;
						this._bufferOffset++;
						//if ( !this._scalarBuffer.IsFilled )
						if ( this._bufferOffset < this._primitiveSize )
						{
							// no transition.
							continue;
						}

						// new collection

						//if ( this._scalarBuffer.AsUInt32() == 0 )
						var asUInt32 = this.GetPrimitiveBufferAsUInt32();
						if ( asUInt32 == 0 )
						{
							// empty collection
							var temp = this.AddToContextCollection( CreateEmptyCollection( this._contextValueHeader ), trace );
							if ( temp.HasValue )
							{
								result = temp.Value;
								break;
							}
							else
							{
								continue;
							}
						}
						else
						{
							//this._collectionState.NewContextCollection( this._contextValueHeader, this._scalarBuffer.AsUInt32() );
							this._collectionState.NewContextCollection( this._contextValueHeader, asUInt32 );
							// Collection length might be ( 5 byte ) * ( items count )
							//segmentatedSource.NotifySegmentLength( this._scalarBuffer.AsUInt32() * sizeof( int ) );
							segmentatedSource.NotifySegmentLength( asUInt32 * sizeof( int ) );
							this.TransitToUnpackContextCollection();
							continue;
						}
					}
					case Stage.UnpackRawLength:
					{
						//this._scalarBuffer = this._scalarBuffer.Feed( b );
						this._primitiveBuffer[ this._bufferOffset ] = b;
						this._bufferOffset++;
						//if ( !this._scalarBuffer.IsFilled )
						if ( this._bufferOffset < this._primitiveSize )
						{
							// no transition.
							continue;
						}

						this.TransitToUnpackRawBytes( segmentatedSource, this.GetPrimitiveBufferAsUInt32(), trace );
						continue;
					}
					case Stage.UnpackRawBytes:
					{
						Contract.Assert( ( this._contextValueHeader.Type & MessageType.IsRawBinary ) != 0, this._contextValueHeader.ToString() );
						//Contract.Assert( this._scalarBuffer.BackingStore != null, this._scalarBuffer.ToString() );
						Contract.Assert( this._rawBuffer != null );

						// TODO: Bulk read
						//this._scalarBuffer = this._scalarBuffer.Feed( b );
						this._rawBuffer[ this._bufferOffset ] = b;
						this._bufferOffset++;
						//if ( !this._scalarBuffer.IsFilled )
						if ( this._bufferOffset < this._rawBuffer.Length )
						{
							// no transition.
							continue;
						}

						//var temp = this.AddToContextCollection( this._scalarBuffer.AsMessagePackObject( this._contextValueHeader.Type ), trace );
						var temp = this.AddToContextCollection( new MessagePackObject( this._rawBuffer, true ), trace );
						if ( temp.HasValue )
						{
							result = temp.Value;
							break;
						}
						else
						{
							continue;
						}
					}
					case Stage.UnpackScalar:
					{
						Contract.Assert( ( this._contextValueHeader.Type & MessageType.IsVariable ) != 0, this._contextValueHeader.ToString() );
						Contract.Assert( ( this._contextValueHeader.Type & MessageType.IsCollection ) == 0, this._contextValueHeader.ToString() );
						//Contract.Assert( this._scalarBuffer.BackingStore != null, this._scalarBuffer.ToString() );

						//this._scalarBuffer = this._scalarBuffer.Feed( b );
						this._primitiveBuffer[ this._bufferOffset ] = b;
						this._bufferOffset++;
						//if ( !this._scalarBuffer.IsFilled )
						if ( this._bufferOffset < this._primitiveSize )
						{
							// no transition.
							continue;
						}

						//var temp = this.AddToContextCollection( this._scalarBuffer.AsMessagePackObject( this._contextValueHeader.Type ), trace );
						var temp = this.AddToContextCollection( BytesBuffer.AsMessagePackObject( this._primitiveBuffer, this._contextValueHeader.Type ), trace );
						if ( temp.HasValue )
						{
							result = temp.Value;
							break;
						}
						else
						{
							continue;
						}
					}
					default:
					{
						#region UnpackHeaderAndFixedValue

						var header = _headerArray[ b ];
						this._contextValueHeader = header;
						MessagePackObject newCollectionItem = default( MessagePackObject );
						switch ( this._contextValueHeader.Type )
						{
							case MessageType.Array16:
							case MessageType.Array32:
							case MessageType.Map16:
							case MessageType.Map32:
							{
								this.TransitToUnpackCollectionLength();
								// Try to get length.
								continue;
							}
							case MessageType.Raw16:
							case MessageType.Raw32:
							{
								this.TransitToUnpackRawLength();
								// Try to get length.
								continue;
							}
							case MessageType.FixArray:
							case MessageType.FixMap:
							{
								if ( this._contextValueHeader.ValueOrLength == 0 )
								{
									newCollectionItem = CreateEmptyCollection( this._contextValueHeader );
									break;
								}

								segmentatedSource.NotifySegmentLength( this._contextValueHeader.ValueOrLength );
								this._collectionState.NewContextCollection( this._contextValueHeader, this._contextValueHeader.ValueOrLength );
								this.TransitToUnpackContextCollection();
								// Try to get items.
								continue;
							}
							case MessageType.FixRaw:
							{
								if ( this._contextValueHeader.ValueOrLength == 0 )
								{
									newCollectionItem = Binary.Empty;
									break;
								}

								// this._scalarBuffer = new BytesBuffer( 1 ).Feed( unchecked( ( byte )this._contextValueHeader.ValueOrLength ) );
								//this._primitiveBuffer = this._contextValueHeader.ValueOrLength;
								//this.TransitToUnpackRawBytes( segmentatedSource, trace );
								this.TransitToUnpackRawBytes( segmentatedSource, this._contextValueHeader.ValueOrLength, trace );
								// Try to get body.
								continue;
							}
							case MessageType.Nil:
							{
								newCollectionItem = MessagePackObject.Nil;
								break;
							}
							case MessageType.True:
							{
								newCollectionItem = new MessagePackObject( true );
								break;
							}
							case MessageType.False:
							{
								newCollectionItem = new MessagePackObject( false );
								break;
							}
							case MessageType.NegativeFixNum:
							{
								newCollectionItem = new MessagePackObject( unchecked( ( sbyte )b ) );
								break;
							}
							case MessageType.PositiveFixNum:
							{
								newCollectionItem = new MessagePackObject( b );
								break;
							}
							default:
							{
								this.TransitToUnpackScalar();
								// Try to get body.
								continue;
							}
						}

						var built = this.AddToContextCollection( newCollectionItem, trace );
						if ( built == null )
						{
							continue;
						}
						else
						{
							result = built.Value;
							break;
						}

						#endregion UnpackHeaderAndFixedValue
					}
				}

				#endregion Transit
#else
				MessagePackObject? result = this.TransitStage( b, segmentatedSource, trace );
#endif

#if !INLINED
				if ( result != null )
				{
#endif
				if ( this._collectionState.IsEmpty )
				{
					this._stage = Stage.Root;
					Contract.Assert( this._contextValueHeader.Type == MessageType.Unknown, this._contextValueHeader.ToString() );// null
					// Contract.Assert( this._scalarBuffer.BackingStore == null, this._scalarBuffer.ToString() ); // null
					Contract.Assert( this._rawBuffer == null );
					return result;
				}
#if !INLINED
				}
#endif
			}
#if PERF_TRACE
			}
			finally
			{
				var e = System.Diagnostics.Stopwatch.GetTimestamp() - t;
				trace.Add( new TraceEntry( TracePoint.Finish ) );
				using ( var writer = new System.IO.StreamWriter( ".\\sutrace.txt" ) )
				{
					writer.WriteLine( "Unpack:: {0:#,0}steps.", e );
					writer.WriteLine( "Trace({0:#,0})", trace.Count );
					foreach ( var item in trace )
					{
						writer.WriteLine( "{0:#,0}\t{1}", item.Timestamp, item.TracePoint );
					}
					writer.Flush();
				}
			}
#endif
			return null;
		}

		private uint GetPrimitiveBufferAsUInt32()
		{
			switch ( this._primitiveSize )
			{
				case 1:
				{
					return BigEndianBinary.ToByte( this._primitiveBuffer, 0 );
				}
				case 2:
				{
					return BigEndianBinary.ToUInt16( this._primitiveBuffer, 0 );
				}
				default:
				{
					Contract.Assert( this._primitiveSize == 4, this._primitiveSize.ToString() );
					return BigEndianBinary.ToUInt32( this._primitiveBuffer, 0 );
				}
			}
		}

		/// <summary>
		///		Process state machine transition with required operation.
		/// </summary>
		/// <param name="b">Byte which was supplied.</param>
		/// <param name="source"><see cref="ISegmentLengthRecognizeable"/> to be notified.</param>
		/// <returns>
		///		If root or context collection is fully unpacked, then it.
		///		Otherwise null.
		/// </returns>
		private MessagePackObject? TransitStage( byte b, ISegmentLengthRecognizeable source, List<TraceEntry> trace )
		{
			//return null;
			//trace.Add( new TraceEntry( TracePoint.TransitStage ) );
			switch ( this._stage )
			{
				case Stage.UnpackCollectionLength:
				{
					//this._scalarBuffer = this._scalarBuffer.Feed( b );
					this._primitiveBuffer[ this._bufferOffset ] = b;
					this._bufferOffset++;
					//if ( !this._scalarBuffer.IsFilled )
					if ( this._bufferOffset < this._primitiveSize )
					{
						// no transition.
						return null;
					}

					// new collection

					var asUInt32 = this.GetPrimitiveBufferAsUInt32();
					//if ( this._scalarBuffer.AsUInt32() == 0 )
					if ( asUInt32 == 0 )
					{
						// empty collection
						return this.AddToContextCollection( CreateEmptyCollection( this._contextValueHeader ), trace );
					}
					else
					{
						//this._collectionState.NewContextCollection( this._contextValueHeader, this._scalarBuffer.AsUInt32() );
						this._collectionState.NewContextCollection( this._contextValueHeader, asUInt32 );
						// Collection length might be ( 5 byte ) * ( items count )
						//source.NotifySegmentLength( this._scalarBuffer.AsUInt32() * sizeof( int ) );
						source.NotifySegmentLength( asUInt32 * sizeof( int ) );
						this.TransitToUnpackContextCollection();
						return null;
					}
				}
				case Stage.UnpackRawLength:
				{
					//this._scalarBuffer = this._scalarBuffer.Feed( b );
					this._primitiveBuffer[ this._bufferOffset ] = b;
					this._bufferOffset++;
					//if ( !this._scalarBuffer.IsFilled )
					if ( this._bufferOffset < this._primitiveSize )
					{
						// no transition.
						return null;
					}

					this.TransitToUnpackRawBytes( source, this.GetPrimitiveBufferAsUInt32(), trace );
					return null;
				}
				case Stage.UnpackRawBytes:
				{
					Contract.Assert( ( this._contextValueHeader.Type & MessageType.IsRawBinary ) != 0, this._contextValueHeader.ToString() );
					//Contract.Assert( this._scalarBuffer.BackingStore != null, this._scalarBuffer.ToString() );

					// TODO: Bulk read
					//this._scalarBuffer = this._scalarBuffer.Feed( b );
					this._rawBuffer[ this._bufferOffset ] = b;
					this._bufferOffset++;
					//if ( !this._scalarBuffer.IsFilled )
					if ( this._bufferOffset < this._rawBuffer.Length  )
					{
						// no transition.
						return null;
					}

					//return this.AddToContextCollection( this._scalarBuffer.AsMessagePackObject( this._contextValueHeader.Type ), trace );
					return this.AddToContextCollection( new MessagePackObject( this._rawBuffer, true ), trace );
				}
				case Stage.UnpackScalar:
				{
					Contract.Assert( ( this._contextValueHeader.Type & MessageType.IsVariable ) != 0, this._contextValueHeader.ToString() );
					Contract.Assert( ( this._contextValueHeader.Type & MessageType.IsCollection ) == 0, this._contextValueHeader.ToString() );
					//Contract.Assert( this._scalarBuffer.BackingStore != null, this._scalarBuffer.ToString() );

					//this._scalarBuffer = this._scalarBuffer.Feed( b );
					this._primitiveBuffer[ this._bufferOffset ] = b;
					this._bufferOffset++;
					//if ( !this._scalarBuffer.IsFilled )
					if ( this._bufferOffset < this._primitiveSize )
					{
						// no transition.
						return null;
					}

					//return this.AddToContextCollection( this._scalarBuffer.AsMessagePackObject( this._contextValueHeader.Type ), trace );
					return this.AddToContextCollection( BytesBuffer.AsMessagePackObject( this._primitiveBuffer, this._contextValueHeader.Type ), trace );
				}
				default:
				{
					return this.UnpackHeaderAndFixedValue( b, source, trace );
				}
			}
		}

		/// <summary>
		///		Transit current stage to <see cref="Stage.UnpackScalar"/> with cleanuping states.
		/// </summary>
		private void TransitToUnpackScalar()
		{
			this._stage = Stage.UnpackScalar;
			//this._scalarBuffer = new BytesBuffer( GetLength( this._contextValueHeader.Type ) );
			this._bufferOffset = 0;
			this._primitiveSize = GetLength( this._contextValueHeader.Type );
		}

		/// <summary>
		///		Transit current stage to <see cref="Stage.UnpackCollectionLength"/> with cleanuping states.
		/// </summary>
		private void TransitToUnpackCollectionLength()
		{
			this._stage = Stage.UnpackCollectionLength;
			//this._scalarBuffer = new BytesBuffer( GetLength( this._contextValueHeader.Type ) );
			this._bufferOffset = 0;
			this._primitiveSize = GetLength( this._contextValueHeader.Type );
		}

		/// <summary>
		///		Transit current stage to <see cref="Stage.UnpackCollectionLength"/> with cleanuping states.
		/// </summary>
		private void TransitToUnpackRawLength()
		{
			this._stage = Stage.UnpackRawLength;
			//this._scalarBuffer = new BytesBuffer( GetLength( this._contextValueHeader.Type ) );
			this._bufferOffset = 0;
			this._primitiveSize = GetLength( this._contextValueHeader.Type );
		}

		/// <summary>
		///		Transit current stage to <see cref="Stage.UnpackRawBytes"/> with cleanuping states.
		/// </summary>
		/// <param name="source"><see cref="ISegmentLengthRecognizeable"/> to be notified.</param>
		private void TransitToUnpackRawBytes( ISegmentLengthRecognizeable source,
			uint length,
			List<TraceEntry> trace )
		{
#if PERF_TRACE
			trace.Add( new TraceEntry( TracePoint.TransitToUnpackRawBytes ) );
#endif
			this._stage = Stage.UnpackRawBytes;
			//if ( this._contextValueHeader.ValueOrLength == 0 )
			//{
			//    // Length is gotten, so set it.
			//    // this._contextValueHeader = this._contextValueHeader.SetLength( this._scalarBuffer.AsUInt32() );
			//    this._contextValueHeader = this._contextValueHeader.SetLength( length );
			//}

			// Allocate buffer to store raw binaries.
			//source.NotifySegmentLength( this._contextValueHeader.ValueOrLength );
			//this._scalarBuffer = new BytesBuffer( this._contextValueHeader.ValueOrLength );
			source.NotifySegmentLength( length );
			this._rawBuffer = new byte[ length ];
			this._bufferOffset = 0;
		}

		/// <summary>
		///		Transit current stage to <see cref="Stage.UnpackContextCollection"/> with cleanuping states.
		/// </summary>
		private void TransitToUnpackContextCollection()
		{
			this._stage = Stage.UnpackContextCollection;
			this._contextValueHeader = MessagePackHeader.Null;
			//this._scalarBuffer = BytesBuffer.Null;
			Array.Clear( this._primitiveBuffer, 0, this._primitiveBuffer.Length );
			this._bufferOffset = 0;
			this._rawBuffer = null;
		}

		/// <summary>
		///		Create <see cref="MessagePackObject"/> which wraps appropriate empty collection.
		/// </summary>
		/// <param name="header">Header which has type information.</param>
		/// <returns><see cref="MessagePackObject"/> which wraps appropriate empty collection.</returns>
		private MessagePackObject CreateEmptyCollection( MessagePackHeader header )
		{
			Contract.Assert( header.ValueOrLength == 0, header.ToString() );

			if ( ( header.Type & MessageType.IsArray ) != 0 )
			{
				return new MessagePackObject( new List<MessagePackObject>( 0 ) );
			}
			else
			{
				return new MessagePackObject( new Dictionary<MessagePackObject, MessagePackObject>( 0 ) );
			}
		}

		/// <summary>
		///		Retrieve header from specified byte, and returns value itself if it is determined only header value.
		///		This method update instance state with retrieved header.
		/// </summary>
		/// <param name="b">Byte which must be header value.</param>
		/// <param name="source"><see cref="ISegmentLengthRecognizeable"/> to be notified.</param>
		/// <returns>
		///		if value is determined only header then unpacked value,
		///		otherwise null.
		/// </returns>
		private MessagePackObject? UnpackHeaderAndFixedValue( byte b, ISegmentLengthRecognizeable source, List<TraceEntry> trace )
		{
#if PERF_TRACE
			trace.Add( new TraceEntry( TracePoint.UnpackHeaderAndFixedValue ) );
#endif
			Contract.Assert( this._stage == Stage.Root || this._stage == Stage.UnpackContextCollection, this._stage.ToString() );
			Contract.Assert( this._contextValueHeader.Type == MessageType.Unknown, this._contextValueHeader.ToString() );// null
			//Contract.Assert( this._scalarBuffer.BackingStore == null, this._scalarBuffer.ToString() ); // null
			Contract.Assert( this._rawBuffer == null );

			this._contextValueHeader = _headerArray[ b ]; // ParseHeader( b, trace );
			Contract.Assert( this._contextValueHeader.Type != MessageType.Unknown, b.ToString( "x2" ) );
			switch ( this._contextValueHeader.Type )
			{
				case MessageType.Array16:
				case MessageType.Array32:
				case MessageType.Map16:
				case MessageType.Map32:
				{
					this.TransitToUnpackCollectionLength();
					// Try to get length.
					return null;
				}
				case MessageType.Raw16:
				case MessageType.Raw32:
				{
					this.TransitToUnpackRawLength();
					// Try to get length.
					return null;
				}
				case MessageType.FixArray:
				case MessageType.FixMap:
				{
					if ( this._contextValueHeader.ValueOrLength == 0 )
					{
						return this.AddToContextCollection( CreateEmptyCollection( this._contextValueHeader ), trace );
					}

					source.NotifySegmentLength( this._contextValueHeader.ValueOrLength );
					this._collectionState.NewContextCollection( this._contextValueHeader, this._contextValueHeader.ValueOrLength );
					this.TransitToUnpackContextCollection();
					// Try to get items.
					return null;
				}
				case MessageType.FixRaw:
				{
					if ( this._contextValueHeader.ValueOrLength == 0 )
					{
						return this.AddToContextCollection( Binary.Empty, trace );
					}

					//this._scalarBuffer = new BytesBuffer( 1 ).Feed( unchecked( ( byte )this._contextValueHeader.ValueOrLength ) );
					//this.TransitToUnpackRawBytes( source, trace );
					this.TransitToUnpackRawBytes( source, this._contextValueHeader.ValueOrLength, trace );
					// Try to get body.
					return null;
				}
				case MessageType.Nil:
				{
					return this.AddToContextCollection( MessagePackObject.Nil, trace );
				}
				case MessageType.True:
				{
					return this.AddToContextCollection( new MessagePackObject( true ), trace );
				}
				case MessageType.False:
				{
					return this.AddToContextCollection( new MessagePackObject( false ), trace );
				}
				case MessageType.NegativeFixNum:
				{
					return this.AddToContextCollection( new MessagePackObject( unchecked( ( sbyte )b ) ), trace );
				}
				case MessageType.PositiveFixNum:
				{
					return this.AddToContextCollection( new MessagePackObject( b ), trace );
				}
				default:
				{
					this.TransitToUnpackScalar();
					// Try to get body.
					return null;
				}
			}
		}

		private static readonly MessagePackHeader[] _headerArray = InitializeHeaderArray();
		private static MessagePackHeader[] InitializeHeaderArray()
		{
			MessagePackHeader[] result = new MessagePackHeader[ 0x100 ];

			for ( int i = 0; i < 0x80; i++ )
			{
				result[ i ] = new MessagePackHeader( MessageType.PositiveFixNum, i );
			}
			for ( int i = 0x80; i < 0x90; i++ )
			{
				result[ i ] = new MessagePackHeader( MessageType.FixMap, i & 0x0f );
			}
			for ( int i = 0x90; i < 0xa0; i++ )
			{
				result[ i ] = new MessagePackHeader( MessageType.FixArray, i & 0x0f );
			}
			for ( int i = 0xa0; i < 0xc0; i++ )
			{
				result[ i ] = new MessagePackHeader( MessageType.FixRaw, i & 0x1f );
			}
			result[ 0xc0 ] = MessageType.Nil;
			// 0xc1 : Undefined
			result[ 0xc2 ] = MessageType.False;
			result[ 0xc3 ] = MessageType.True;
			// 0xc4-0xc9 : Undefined
			result[ 0xca ] = MessageType.Single;
			result[ 0xcb ] = MessageType.Double;
			result[ 0xcc ] = MessageType.UInt8;
			result[ 0xcd ] = MessageType.UInt16;
			result[ 0xce ] = MessageType.UInt32;
			result[ 0xcf ] = MessageType.UInt64;
			result[ 0xd0 ] = MessageType.Int8;
			result[ 0xd1 ] = MessageType.Int16;
			result[ 0xd2 ] = MessageType.Int32;
			result[ 0xd3 ] = MessageType.Int64;
			// 0xd4-0xd9 : Undefined
			result[ 0xda ] = MessageType.Raw16;
			result[ 0xdb ] = MessageType.Raw32;
			result[ 0xdc ] = MessageType.Array16;
			result[ 0xdd ] = MessageType.Array32;
			result[ 0xde ] = MessageType.Map16;
			result[ 0xdf ] = MessageType.Map32;
			for ( int i = 0xe0; i < 0x100; i++ )
			{
				result[ i ] = new MessagePackHeader( MessageType.NegativeFixNum, i & 0x1f );
			}

			//Debug.WriteLine( "Headers:{0}{1}", Environment.NewLine, String.Join( Environment.NewLine, result ) );

			return result;
		}

		/// <summary>
		///		Parse packed Message Pack object header.
		/// </summary>
		/// <param name="b">Byte to be parsed.</param>
		/// <returns><see cref="MessagePackHeader"/>.</returns>
		private static MessagePackHeader ParseHeader( byte b, List<TraceEntry> trace )
		{
#if PERF_TRACE
			trace.Add( new TraceEntry( TracePoint.ParseHeader ) );
#endif
			// 0xxxxxxx
			if ( ( b & 0x80 ) == 0 )
			{
				return new MessagePackHeader( MessageType.PositiveFixNum, ( b & 0x7f ) );
			}

			// 1xxxxxxx

			if ( ( b & 0x40 ) == 0 )
			{
				// 10xxxxxx

				if ( ( b & 0x20 ) != 0 )
				{
					// 101xxxxx
					return new MessagePackHeader( MessageType.FixRaw, ( b & 0x1f ) );
				}
				else if ( ( b & 0x10 ) != 0 )
				{
					// 1001xxxx
					return new MessagePackHeader( MessageType.FixArray, ( b & 0x0f ) );
				}
				else
				{
					// 1000xxxx
					return new MessagePackHeader( MessageType.FixMap, ( b & 0x0f ) );
				}
			}

			// 11xxxxxx

			if ( ( b & 0x20 ) != 0 )
			{
				// 111xxxxx
				return new MessagePackHeader( MessageType.NegativeFixNum, ( b & 0x1f ) );
			}

			// 110xxxxx
			switch ( b & 0x1f )
			{
				case 0x0:
				{
					return MessageType.Nil;
				}
				case 0x2:
				{
					return MessageType.False;
				}
				case 0x3:
				{
					return MessageType.True;
				}
				case 0xa:
				{
					return MessageType.Single;
				}
				case 0xb:
				{
					return MessageType.Double;
				}
				case 0xc:
				{
					return MessageType.UInt8;
				}
				case 0xd:
				{
					return MessageType.UInt16;
				}
				case 0xe:
				{
					return MessageType.UInt32;
				}
				case 0xf:
				{
					return MessageType.UInt64;
				}
				case 0x10:
				{
					return MessageType.Int8;
				}
				case 0x11:
				{
					return MessageType.Int16;
				}
				case 0x12:
				{
					return MessageType.Int32;
				}
				case 0x13:
				{
					return MessageType.Int64;
				}
				case 0x1a:
				{
					return MessageType.Raw16;
				}
				case 0x1b:
				{
					return MessageType.Raw32;
				}
				case 0x1c:
				{
					return MessageType.Array16;
				}
				case 0x1d:
				{
					return MessageType.Array32;
				}
				case 0x1e:
				{
					return MessageType.Map16;
				}
				case 0x1f:
				{
					return MessageType.Map32;
				}
				default:
				{
					throw new UnpackException( String.Format( "Unknown type: 0x{0:x}", b ) );
				}
			}
		}

		/// <summary>
		///		Add unpacked item to context collection.
		///		If context collection is fulfilled, then return it.
		/// </summary>
		/// <param name="item">Item to be added to context collection.</param>
		/// <returns>
		///		If context collection is fulfilled, then return it.
		///		Otherwise null.
		/// </returns>
		private MessagePackObject? AddToContextCollection( MessagePackObject item, List<TraceEntry> trace )
		{
#if PERF_TRACE
			trace.Add( new TraceEntry( TracePoint.AddToContextCollection ) );
#endif
			if ( !this._collectionState.IsEmpty )
			{
				this._collectionState.FeedItem( item );
				if ( this._collectionState.ContextCollectionState.IsFilled )
				{
					// context collection's items have been unpacked.
					this.TransitToUnpackContextCollection();
					return this.AddToContextCollection( this._collectionState.PopCollection(), trace );
				}

				// try to get next item.
				this.TransitToUnpackContextCollection();
				return null;
			}
			else
			{
				// found top level value.
				this.TransitToUnpackContextCollection();
				return item;
			}
		}

		/// <summary>
		///		Get variable portion of header for specified type.
		/// </summary>
		/// <param name="type">Type of message which retrieved from header.</param>
		/// <returns>Size of variable type length. If type is collection or raw, this value indicates size of length portion.</returns>
		private static uint GetLength( MessageType type )
		{
			switch ( type )
			{
				case MessageType.Int8:
				case MessageType.UInt8:
				{
					return sizeof( byte );
				}
				case MessageType.Array16:
				case MessageType.Int16:
				case MessageType.Map16:
				case MessageType.Raw16:
				case MessageType.UInt16:
				{
					return sizeof( ushort );
				}
				case MessageType.Array32:
				case MessageType.Int32:
				case MessageType.Map32:
				case MessageType.Raw32:
				case MessageType.Single:
				case MessageType.UInt32:
				{
					return sizeof( uint );
				}
				case MessageType.Double:
				case MessageType.Int64:
				case MessageType.UInt64:
				{
					return sizeof( ulong );
				}
				default:
				{
					Contract.Assert( false, "FAIL" );
					return 0;
				}
			}
		}

		/// <summary>
		///		Represents state machine stage (state) of <see cref="StreamingUnpacker"/>.
		/// </summary>
		private enum Stage
		{
			/// <summary>
			///		State machine stays root unpacking.
			///		<see cref="StreamingUnpacker"/> does not have any intermediate state.
			///		This is initial state.
			/// </summary>
			Root = 0,

			/// <summary>
			///		State machine is unpacking some collection.
			///		<see cref="StreamingUnpacker"/> will unpack next item of context collection.
			/// </summary>
			UnpackContextCollection,

			/// <summary>
			///		State machine is unpacking length of array or map.
			///		<see cref="StreamingUnpacker"/> will unpack scalar as length of collection, 
			///		then add new context collection to the stack and unpack items.
			/// </summary>
			UnpackCollectionLength,

			/// <summary>
			///		State machine is unpacking length of raw binaries.
			///		<see cref="StreamingUnpacker"/> will unpack scalar as length of binaries, 
			///		then get following bytes as value.
			/// </summary>
			UnpackRawLength,

			/// <summary>
			///		State machine is getting bytes as raw binaries.
			/// </summary>
			UnpackRawBytes,

			/// <summary>
			///		State machine is unpacking body of scalar value.
			/// </summary>
			UnpackScalar
		}

		/// <summary>
		///		Represents type of message.
		/// </summary>
		private enum MessageType : ushort
		{
			/// <summary>
			///		Type is not known yet.
			/// </summary>
			Unknown = 0,
			Nil = 10,
			PositiveFixNum = 20,
			NegativeFixNum = 21,
			UInt8 = IsVariable | 30,
			UInt16 = IsVariable | 31,
			UInt32 = IsVariable | 32,
			UInt64 = IsVariable | 33,
			Int8 = IsVariable | 40,
			Int16 = IsVariable | 41,
			Int32 = IsVariable | 42,
			Int64 = IsVariable | 43,
			FixRaw = IsRawBinary | IsCollection | 50,
			Raw16 = IsVariable | IsRawBinary | IsCollection | 51,
			Raw32 = IsVariable | IsRawBinary | IsCollection | 52,
			Single = IsVariable | 60,
			Double = IsVariable | 61,
			False = 70,
			True = 71,
			FixArray = IsArray | IsCollection | 80,
			Array16 = IsVariable | IsArray | IsCollection | 81,
			Array32 = IsVariable | IsArray | IsCollection | 82,
			FixMap = IsMap | IsCollection | 90,
			Map16 = IsVariable | IsMap | IsCollection | 91,
			Map32 = IsVariable | IsMap | IsCollection | 92,
			// TODO: string
			// TODO: Fixed-Typed Array

			/// <summary>
			///		Flag indicates type is variable, so length unpacking is required.
			/// </summary>
			IsVariable = 0x400,

			/// <summary>
			///		Flag indicates type is collection, so context collection management for nesting is required.
			/// </summary>
			IsCollection = 0x800,

			/// <summary>
			///		Flag indicates type is a type of array.
			/// </summary>
			IsArray = 0x1000,

			/// <summary>
			///		Flag indicates type is a type of map.
			/// </summary>
			IsMap = 0x2000,

			/// <summary>
			///		Flag indicates type is a type of raw binary.
			/// </summary>
			IsRawBinary = 0x4000,
		}

		/// <summary>
		///		Lightweight structured header representation.
		///		Note that this is VALUE type.
		/// </summary>
		private struct MessagePackHeader
		{
			/// <summary>
			///		Null value.
			/// </summary>
			public static readonly MessagePackHeader Null = new MessagePackHeader();

			private readonly MessageType _type;

			/// <summary>
			///		Get type of message.
			/// </summary>
			/// <value>Type of message.</value>
			public MessageType Type
			{
				get { return this._type; }
			}

			private readonly uint _valueOrLength;

			/// <summary>
			///		Get value of fixed scalar value, length of fixed collections,
			///		length of fixed raw binary, or length of variable length.
			/// </summary>
			public uint ValueOrLength
			{
				get { return this._valueOrLength; }
			}

			public MessagePackHeader( MessageType type, int valueOrLength )
				: this( type, ToUInt32( valueOrLength ) ) { }

			private static uint ToUInt32( int valueOrLength )
			{
				Contract.Assert( valueOrLength >= 0 );
				return unchecked( ( uint )valueOrLength );
			}

			public MessagePackHeader( MessageType type, uint valueOrLength )
			{
				this._type = type;
				this._valueOrLength = valueOrLength;
			}

			public override string ToString()
			{
				return this._type + ":" + this._valueOrLength;
			}

			/// <summary>
			///		Set recognized length of non-fixed collection or binary.
			/// </summary>
			/// <param name="recognizedLength">Recognized length.</param>
			/// <returns></returns>
			public MessagePackHeader SetLength( uint recognizedLength )
			{
				Contract.Assert( this._valueOrLength == 0, this.ToString() );

				return new MessagePackHeader( this._type, unchecked( ( uint )recognizedLength ) );
			}

			public static implicit operator MessagePackHeader( MessageType type )
			{
				return new MessagePackHeader( type, 0 );
			}
		}

		/// <summary>
		///		Represents a set of states for unpackaging context collection.
		/// </summary>
		private sealed class CollectionUnpackagingState
		{
			/// <summary>
			///		Stack of collection context.
			/// </summary>
			private readonly Stack<CollectionContextState> _collectionContextStack = new Stack<CollectionContextState>();

			/// <summary>
			///		Get the value indicates whether internal context stack is empty.
			/// </summary>
			/// <value>
			///		If internal context stack is empty then true.
			/// </value>
			/// <remarks>
			///		If this property returns true when you complete unpackaging context collection,
			///		it indicates that entire object tree has been unpackaged.
			/// </remarks>
			public bool IsEmpty
			{
				get { return this._collectionContextStack.Count == 0; }
			}

			/// <summary>
			///		Get state of context collection.
			/// </summary>
			/// <value>State of context collection.</value>
			public CollectionContextState ContextCollectionState
			{
				get
				{
					Contract.Assert( !this.IsEmpty );
					return this._collectionContextStack.Peek();
				}
			}

			/// <summary>
			///		Initialize new instance.
			/// </summary>
			public CollectionUnpackagingState() { }

			/// <summary>
			///		Push new context collection state to internal stack.
			/// </summary>
			/// <param name="header">Header of collection object.</param>
			/// <param name="count">Items count of collection object. If collection is map, this value indicates count of entries.</param>
			public void NewContextCollection( MessagePackHeader header, uint count )
			{
				Contract.Assert( ( header.Type & MessageType.IsRawBinary ) == 0, header.Type.ToString() );

				this._collectionContextStack.Push( new CollectionContextState( header, count ) );
			}

			/// <summary>
			///		Pop context collection state from internal stack, 
			///		and return <see cref="MessagePackObject"/> which represents popped context collection.
			/// </summary>
			/// <returns></returns>
			public MessagePackObject PopCollection()
			{
				Contract.Assert( !this.IsEmpty );

				var context = this._collectionContextStack.Pop();
				if ( ( context.Header.Type & MessageType.IsArray ) != 0 )
				{
					Contract.Assert( context.Items != null );
					Contract.Assert( context.Unpacked == context.Capacity );
					return new MessagePackObject( context.Items );
				}
				else if ( ( context.Header.Type & MessageType.IsMap ) != 0 )
				{
					//Contract.Assert( context.Items.Length % 2 == 0, context.Items.Length.ToString() );
					//Dictionary<MessagePackObject, MessagePackObject> dictionary = new Dictionary<MessagePackObject, MessagePackObject>( context.Items.Length / 2 );
					//for ( int i = 0; i < context.Items.Length; i += 2 )
					//{
					//    if ( dictionary.ContainsKey( context.Items[ i ] ) )
					//    {
					//        throw new InvalidMessagePackStreamException( String.Format( CultureInfo.CurrentCulture, "Key '{0}' is duplicated.", context.Items[ i ] ) );
					//    }

					//    dictionary.Add( context.Items[ i ], context.Items[ i + 1 ] );
					//}

					//return new MessagePackObject( dictionary );
					Contract.Assert( context.Dictionary != null );
					Contract.Assert( context.Unpacked == context.Capacity * 2 );
					return new MessagePackObject( context.Dictionary );
				}
				else
				{
					throw new MessageTypeException( String.Format( CultureInfo.CurrentCulture, "Unknown collection type: {0}(0x{0:x})", context.Header.Type ) );
				}
			}

			/// <summary>
			///		Feed new collection item to context collection state.
			/// </summary>
			/// <param name="item">New item to feed.</param>
			public void FeedItem( MessagePackObject item )
			{
				//var context = this._collectionContextStack.Pop();
				//this._collectionContextStack.Push( context.AddUnpackedItem( item ) );
				this._collectionContextStack.Peek().AddUnpackedItem( item );
			}

			/// <summary>
			///		Represents context collection state.
			/// </summary>
			//public struct CollectionContextState
			public sealed class CollectionContextState
			{
				private readonly MessagePackHeader _header;

				/// <summary>
				///		Get header of this collection.
				/// </summary>
				/// <value>Header of this collection.</value>
				public MessagePackHeader Header
				{
					get { return this._header; }
				}

				private readonly Dictionary<MessagePackObject, MessagePackObject> _dictionary;

				public Dictionary<MessagePackObject, MessagePackObject> Dictionary
				{
					get { return this._dictionary; }
				}

				private MessagePackObject? _key;

				private readonly MessagePackObject[] _items;

				/// <summary>
				///		Get storage for items of this collection.
				/// </summary>
				/// <value>
				///		Storage for items of this collection.
				///		Do not modify this array directly.
				/// </value>
				public MessagePackObject[] Items
				{
					get { return this._items; }
				}

				private readonly long _capacity;

				public long Capacity
				{
					get { return this._capacity; }
				}

				private long _unpacked;

				/// <summary>
				///		Get count of unpackaged items.
				/// </summary>
				/// <value>
				///		 Count of unpackaged items.
				/// </value>
				public long Unpacked
				{
					get { return this._unpacked; }
				}

				/// <summary>
				///		Get the value which indicates <see cref="Items"/> are filled.
				/// </summary>
				/// <value>If <see cref="Items"/> are filled then true.</value>
				public bool IsFilled
				{
					get { return this._unpacked == this._capacity * ( this._items != null ? 1 : 2 ); }
				}

				/// <summary>
				///		Initialize new instance.
				/// </summary>
				/// <param name="header">Header of collection.</param>
				/// <param name="count">Recognized count of items in this collection.</param>
				public CollectionContextState( MessagePackHeader header, uint count )
				{
					Contract.Assert( header.Type != MessageType.Unknown );

					this._header = header;
					if ( ( header.Type & MessageType.IsMap ) == 0 )
					{
						Contract.Assert( ( header.Type & MessageType.IsArray ) != 0 );
						this._items = new MessagePackObject[ count ];
					}
					else
					{
						Contract.Assert( ( header.Type & MessageType.IsArray ) == 0 );
						if ( Int32.MaxValue < count )
						{
							throw new NotImplementedException( "Maps over 2^31 items are not supported yet." );
						}

						this._dictionary = new Dictionary<MessagePackObject, MessagePackObject>( unchecked( ( int )( count & 0x7fffffff ) ) );
					}

					this._capacity = count;
					this._unpacked = 0;
				}

				/// <summary>
				///		Initialize new instance.
				/// </summary>
				/// <param name="header">Header of collection.</param>
				/// <param name="items">Existent items storage.</param>
				/// <param name="unpacked">Recognized count of items in this collection.</param>
				//private CollectionContextState( MessagePackHeader header, MessagePackObject[] items, long unpacked )
				//{
				//    Contract.Assert( header.Type != MessageType.Unknown );
				//    Contract.Assert( items != null );
				//    Contract.Assert( unpacked <= items.Length );

				//    this._header = header;
				//    this._items = items;
				//    this._unpacked = unpacked;
				//}

				/// <summary>
				///		Returns string representation of this object.
				/// </summary>
				/// <returns>
				///		String which format is "<em>Header</em>(<em>Unpacked</em>/<em>Length</em>)".
				/// </returns>
				public override string ToString()
				{
					//if ( this._items == null )
					//{
					//    return "(null)";
					//}
					//else
					//{
					//    return String.Format( CultureInfo.CurrentCulture, "{0}({1}/{2})", this._header, this._unpacked, this._items.Length );
					//}
					if ( this._items != null )
					{
						return String.Format( CultureInfo.CurrentCulture, "{0}({1}/{2})", this._header, this._unpacked, this._capacity );
					}
					else
					{
						return String.Format( CultureInfo.CurrentCulture, "{0}({1}/{2})", this._header, this._unpacked / 2.0, this._capacity );
					}
				}

				/// <summary>
				///		Add unpackaged item to this collection.
				/// </summary>
				/// <param name="item">Item to be added.</param>
				/// <returns>New context state to replace this instance.</returns>
				public CollectionContextState AddUnpackedItem( MessagePackObject item )
				{
					Contract.Assert(
						( ( this._items != null && this._unpacked < this._capacity )
						|| ( this._dictionary != null && this._unpacked < this._capacity * 2 )
						),
						this._items != null
						? this._unpacked + "<" + this._capacity
						: this._unpacked + "<" + this._capacity * 2
					);

					if ( this._items != null )
					{
						this._items[ this._unpacked ] = item;
					}
					else
					{
						if ( this._key == null )
						{
							this._key = item;
						}
						else
						{
							try
							{
								this._dictionary.Add( this._key.Value, item );
							}
							catch ( ArgumentException )
							{
								throw new InvalidMessagePackStreamException( String.Format( CultureInfo.CurrentCulture, "Key '{0}' is duplicated.", this._key ) );
							}

							this._key = null;
						}
					}

					//return new CollectionContextState( this._header, this._items, this._unpacked + 1 );
					this._unpacked++;
					return this;
				}
			}
		}

		/// <summary>
		///		Represents buffer as value type.
		/// </summary>
		private struct BytesBuffer
		{
			/// <summary>
			///		Represents null buffer.
			/// </summary>
			public static BytesBuffer Null = new BytesBuffer();

			private readonly byte[] _backingStore;

			/// <summary>
			///		Get backing store of this buffer.
			/// </summary>
			/// <value>
			///		Backing store of this buffer.
			///		DO NOT modify this value directly.
			public byte[] BackingStore
			{
				get { return this._backingStore; }
			}

			private readonly int _position;

			/// <summary>
			///		Get the value which indicates this buffer is filled.
			/// </summary>
			/// <value>If  this buffer is filled then true.</value>
			public bool IsFilled
			{
				get
				{

					return this._backingStore == null ? false : this._position == this._backingStore.Length;
				}
			}

			/// <summary>
			///		Initialize new instance.
			/// </summary>
			/// <param name="length">Length of bytes.</param>
			public BytesBuffer( uint length )
			{
				this._backingStore = new byte[ length ];
				this._position = 0;
			}

			/// <summary>
			///		Initialize new instance.
			/// </summary>
			/// <param name="backingStore">Existent backing store.</param>
			/// <param name="newPosition">Position where this buffer is filled.</param>
			private BytesBuffer( byte[] backingStore, int newPosition )
			{
				this._backingStore = backingStore;
				this._position = newPosition;
			}

			/// <summary>
			///		Returns string representation of this object.
			/// </summary>
			/// <returns>
			///		String which format is "byte[<em>Length</em>]@<em>Position</em>".
			/// </returns>
			public override string ToString()
			{
				if ( this._backingStore == null )
				{
					return "(null)";
				}
				else
				{
					return String.Format( CultureInfo.InvariantCulture, "byte[{0}]@{1}", this._backingStore.Length, this._position );
				}
			}

			/// <summary>
			///		Feed specified byte in this buffer, and increment position.
			/// </summary>
			/// <param name="b">Byte to be feeded.</param>
			/// <returns>New buffer to replace this object.</returns>
			public BytesBuffer Feed( byte b )
			{
				Contract.Assert( this._backingStore != null, this.ToString() );
				Contract.Assert( !this.IsFilled, "Already filled:" + this );

				this._backingStore[ this._position ] = b;
				return new BytesBuffer( this._backingStore, this._position + 1 );
			}

			/// <summary>
			///		Get internal value as <see cref="UInt32"/>.
			/// </summary>
			/// <returns><see cref="UInt32"/> value of this buffer.</returns>
			public uint AsUInt32()
			{
				Contract.Assert( this.IsFilled, "Not filled yet:" + this );

				switch ( this._backingStore.Length )
				{
					case 1:
					{
						return this._backingStore[ 0 ];
					}
					case 2:
					{
						return BigEndianBinary.ToUInt16( this._backingStore, 0 );
					}
					default:
					{
						Contract.Assert( this._backingStore.Length == sizeof( uint ), this._backingStore.Length.ToString() );
						return BigEndianBinary.ToUInt32( this._backingStore, 0 );
					}
				}
			}

			/// <summary>
			///		Get internal buffer as specified <see cref="MessagePackObject"/> numeric primitive.
			/// </summary>
			/// <param name="type">Type of value to be deserialized.</param>
			/// <returns><see cref="MessagePackObject"/> which wraps deserialized numeric primitive.</returns>
			public MessagePackObject AsMessagePackObject( MessageType type )
			{
				Contract.Assert( this.IsFilled, "Not filled yet:" + this );

				return AsMessagePackObject( this._backingStore, type );
			}

			public static MessagePackObject AsMessagePackObject( byte[] buffer, MessageType type )
			{
				switch ( type )
				{
					case MessageType.Double:
					{
						return BigEndianBinary.ToDouble( buffer, 0 );
					}
					case MessageType.Int16:
					{
						return BigEndianBinary.ToInt16( buffer, 0 );
					}
					case MessageType.Int32:
					{
						return BigEndianBinary.ToInt32( buffer, 0 );
					}
					case MessageType.Int64:
					{
						return BigEndianBinary.ToInt64( buffer, 0 );
					}
					case MessageType.Int8:
					{
						return BigEndianBinary.ToSByte( buffer, 0 );
					}
					case MessageType.Single:
					{
						return BigEndianBinary.ToSingle( buffer, 0 );
					}
					case MessageType.UInt16:
					{
						return BigEndianBinary.ToUInt16( buffer, 0 );
					}
					case MessageType.UInt32:
					{
						return BigEndianBinary.ToUInt32( buffer, 0 );
					}
					case MessageType.UInt64:
					{
						return BigEndianBinary.ToUInt64( buffer, 0 );
					}
					case MessageType.UInt8:
					{
						return BigEndianBinary.ToByte( buffer, 0 );
					}
					default:
					{
						return new MessagePackObject( buffer, true );
					}
				}
			}
		}

		/// <summary>
		///		Null object for <see cref="ISegmentLengthRecognizeable"/>.
		/// </summary>
		private sealed class NullSegmentLengthRecognizeable : ISegmentLengthRecognizeable
		{
			public static readonly NullSegmentLengthRecognizeable Instance = new NullSegmentLengthRecognizeable();

			private NullSegmentLengthRecognizeable()
			{
			}

			public void NotifySegmentLength( long lengthFromCurrent )
			{
				// nop
			}
		}

	}
}