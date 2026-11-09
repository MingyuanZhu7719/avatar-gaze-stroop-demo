using System;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class RatingRecordOwnershipTests
    {
        public static void Register(TestSuite suite)
        {
            suite.Run("rating public constructor and defaults remain available without writable setters",()=>{
                Check.True(typeof(RatingRecord).GetConstructor(Type.EmptyTypes)!=null);
                foreach(var property in typeof(RatingRecord).GetProperties())
                {
                    var setter=property.GetSetMethod(true);
                    Check.True(setter==null||setter.IsPrivate,"Unexpected writable setter: "+property.Name);
                }
                var detached=new RatingRecord();
                Check.Equal(0,detached.BlockIndex);Check.Equal(0,detached.Value);Check.Equal(0.0,detached.Time);
                Check.Equal(null,detached.ItemId);Check.Equal(null,detached.Language);Check.Equal(null,detached.Version);
            });
            suite.Run("rating records preserve live collection references and all twenty scale values",()=>{
                var clock=new ManualClock();var experiment=Fixtures.New(clock);Fixtures.Practice(experiment,clock,8);
                var retained=experiment.Ratings;RatingRecord first=null;double firstTime=0;
                for(int block=0;block<4;block++)
                {
                    Fixtures.Start(experiment,clock);
                    for(int trial=0;trial<24;trial++)Fixtures.Answer(experiment,clock);
                    Check.Equal(Phase.Questionnaire,experiment.Phase);
                    for(int rating=1;rating<=5;rating++)
                    {
                        clock.Advance(.2);experiment.Rate(rating);
                        var record=retained[block*5+rating-1];Check.True(Object.ReferenceEquals(record,experiment.Ratings[block*5+rating-1]));
                        Check.Equal(block,record.BlockIndex);Check.Equal(rating,record.Value);Check.Near(clock.Now,record.Time);
                        Check.Equal("en",record.Language);Check.Equal(Questionnaire.Version,record.Version);
                        if(first==null){first=record;firstTime=record.Time;}
                    }
                    clock.Advance(30);Check.True(experiment.Ready());
                }
                Check.Equal(20,retained.Count);Check.Equal(1,first.Value);Check.Near(firstTime,first.Time);
                Check.True(Object.ReferenceEquals(first,experiment.Ratings[0]));Check.Equal(Phase.Ended,experiment.Phase);
            });
        }
    }
}
